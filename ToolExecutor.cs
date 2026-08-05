using System.IO.Compression;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvaBrowser;

/// <summary>
/// Full tool executor ported from UGA tools.py.
/// All operations are restricted to the configured workspace directory.
/// Tools are exposed as Gemini function-calling declarations.
/// </summary>
public static class ToolExecutor
{
    private static string Ws => ConfigManager.WorkspaceDir;

    // Static HTTP client (shared across all async tools)
    private static readonly HttpClient _http = new();

    // Background processes tracking
    private static readonly Dictionary<int, ProcessInfo> _bgProcesses = new();
    private static readonly string _bgLogDir;
    private static readonly string _bgRegistryFile;

    // Undo history
    private static readonly List<UndoEntry> _undoStack = new();
    private const int MAX_UNDO = 50;

    private class ProcessInfo { public int Pid; public string Command; public string LogPath; public DateTime Started; }
    private class UndoEntry { public string FilePath; public string OldContent; public DateTime Time; }

    // Screenshot dir for view_screen/watch_screen
    private static readonly string _screenshotDir;
    private static string? _lastScreenshotPath;

    static ToolExecutor()
    {
        _bgLogDir = Path.Combine(Ws, ".undo_history", "bg_logs");
        _bgRegistryFile = Path.Combine(Ws, ".undo_history", "bg_processes.json");
        _screenshotDir = Path.Combine(Ws, ".undo_history", "screenshots");
        Directory.CreateDirectory(_bgLogDir);
        Directory.CreateDirectory(_screenshotDir);
        // Load any persisted background registry
        LoadBgRegistry();
    }

    /// <summary>
    /// Execute a tool by name with JSON args. Returns (result_text, success).
    /// </summary>
    public static async Task<(string result, bool success)> ExecuteAsync(string toolName, string argsJson, CancellationToken ct = default)
    {
        try
        {
            Dictionary<string, object>? args;
            try { args = JsonConvert.DeserializeObject<Dictionary<string, object>>(argsJson); }
            catch { args = new(); }

            return toolName switch
            {
                // File operations
                "create_file" => CreateFile(args),
                "read_file" => ReadFile(args),
                "edit_file" => EditFile(args),
                "delete_file" => DeleteFile(args),
                "move_file" => MoveFile(args),
                "rename_file" => RenameFile(args),
                "copy_file" => CopyFile(args),
                "create_folder" => CreateFolder(args),

                // Search & Discovery
                "list_files" => ListFiles(args),
                "find_file" => FindFile(args),
                "find_folder" => FindFolder(args),
                "search_in_files" => SearchInFiles(args),
                "file_stats" => FileStats(args),
                "detect_language" => DetectLanguage(args),
                "count_files" => CountFiles(args),
                "count_todos" => CountTodos(args),

                // Bulk editing
                "replace_in_files" => ReplaceInFiles(args),

                // Code quality
                "lint_check" => LintCheck(args),
                "check_file_syntax_all" => CheckFileSyntaxAll(args),

                // Diff
                "diff_preview" => DiffPreview(args),
                "compare_files" => CompareFiles(args),

                // Git
                "git_clone" => await GitCloneAsync(args, ct),
                "git_fetcher" => await GitFetcherAsync(args),
                "git_status" => GitStatus(args),
                "git_diff" => GitDiff(args),
                "git_log" => GitLog(args),
                "git_commit" => GitCommit(args),

                // Execution
                "run_command" => await RunCommandAsync(args, ct),
                "start_background_process" => await StartBackgroundProcessAsync(args),
                "list_background_processes" => ListBackgroundProcesses(),
                "read_background_log" => ReadBackgroundLog(args),
                "stop_background_process" => StopBackgroundProcess(args),
                "wait_process" => await WaitProcessAsync(args, ct),

                // Zip
                "zip_workspace" => ZipWorkspace(args),
                "create_zip" => CreateZip(args),
                "extract_zip" => ExtractZip(args),

                // Undo
                "undo_last_change" => UndoLastChange(),

                // System (gated)
                "Available_Active_Windows" => SystemAccessEnabled(() => ListActiveWindows()),
                "List_System_Processes" => SystemAccessEnabled(() => ListSystemProcesses()),

                // Networking
                "check_port_in_use" => CheckPortInUse(args),
                "http_request" => await HttpRequestAsync(args),

                // Environment
                "env_var_check" => EnvVarCheck(args),

                // Image tools
                "Image_Fetch" => await ImageFetchAsync(args),
                "Image_Fetch_Puter" => await ImageFetchPuterAsync(args),
                "Image_Create" => await ImageCreateAsync(args),
                "Image_Create_Puter" => await ImageCreatePuterAsync(args),

                // Screen tools
                "view_screen" => await ViewScreenAsync(args),
                "view_screen_puter" => await ViewScreenPuterAsync(args),
                "watch_screen" => await WatchScreenAsync(args, ct),

                // Project tools
                "list_dependencies" => ListDependencies(),
                "add_dependency" => await AddDependencyAsync(args, ct),
                "run_tests" => await RunTestsAsync(args, ct),
                "create_test_file" => CreateTestFile(args),
                "generate_readme" => GenerateReadme(args),

                // Python analysis
                "extract_docstrings" => ExtractDocstrings(args),
                "find_unused_imports" => FindUnusedImports(args),

                // Checkpoints
                "save_checkpoint" => SaveCheckpoint(args),
                "load_checkpoint" => LoadCheckpoint(args),
                "list_checkpoints" => ListCheckpoints(),

                // Metrics
                "count_lines_of_code" => CountLinesOfCode(),

                // File conversion
                "convert_file_format" => ConvertFileFormat(args),
                "minify_file" => MinifyFile(args),

                _ => ($"Unknown tool: {toolName}", false),
            };
        }
        catch (Exception ex) { return ($"Tool error: {ex.Message}", false); }
    }

    private static (string, bool) SystemAccessEnabled(Func<(string, bool)> fn)
    {
        if (!ConfigManager.SystemAccessEnabled)
            return ("Permission denied. System access is disabled. Ask the user to enable it in Settings.", false);
        return fn();
    }

    // ===== FILE OPERATIONS =====
    private static (string, bool) CreateFile(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path");
        var content = GetArg(args, "content") ?? "";
        var fp = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fp)!);
        // Save for undo if file exists
        if (File.Exists(fp)) SaveUndo(fp);
        File.WriteAllText(fp, content);
        return ($"Created file: {path}", true);
    }

    private static (string, bool) ReadFile(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path");
        var fp = Resolve(path);
        if (!File.Exists(fp)) return ($"File not found: {path}", false);
        var content = File.ReadAllText(fp);
        if (content.Length > 50000) content = content[..50000] + "\n...(truncated)";
        return (content, true);
    }

    private static (string, bool) EditFile(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path");
        var oldText = GetArg(args, "old_text") ?? "";
        var newText = GetArg(args, "new_text") ?? "";
        var fp = Resolve(path);
        if (!File.Exists(fp)) return ($"File not found: {path}", false);
        var content = File.ReadAllText(fp);
        if (!content.Contains(oldText)) return ($"old_text not found in: {path}", false);
        SaveUndo(fp);
        content = content.Replace(oldText, newText);
        File.WriteAllText(fp, content);
        return ($"Edited file: {path}", true);
    }

    private static (string, bool) DeleteFile(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path");
        var fp = Resolve(path);
        if (File.Exists(fp)) { SaveUndo(fp); File.Delete(fp); return ($"Deleted: {path}", true); }
        if (Directory.Exists(fp)) { Directory.Delete(fp, true); return ($"Deleted folder: {path}", true); }
        return ($"Not found: {path}", false);
    }

    private static (string, bool) MoveFile(Dictionary<string, object> args)
    {
        var src = Resolve(GetArg(args, "source") ?? "");
        var dst = Resolve(GetArg(args, "destination") ?? "");
        if (!File.Exists(src) && !Directory.Exists(src)) return ($"Not found: {src}", false);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(src)) { SaveUndo(src); File.Move(src, dst); }
        else Directory.Move(src, dst);
        return ($"Moved to destination", true);
    }

    private static (string, bool) RenameFile(Dictionary<string, object> args)
    {
        var path = Resolve(GetArg(args, "path") ?? "");
        var newName = GetArg(args, "new_name") ?? "";
        var newPath = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (!File.Exists(path) && !Directory.Exists(path)) return ($"Not found", false);
        SaveUndo(path);
        if (File.Exists(path)) File.Move(path, newPath); else Directory.Move(path, newPath);
        return ($"Renamed to: {newName}", true);
    }

    private static (string, bool) CopyFile(Dictionary<string, object> args)
    {
        var src = Resolve(GetArg(args, "source") ?? "");
        var dst = Resolve(GetArg(args, "destination") ?? "");
        if (!File.Exists(src)) return ($"Not found", false);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
        return ($"Copied", true);
    }

    private static (string, bool) CreateFolder(Dictionary<string, object> args)
    {
        var fp = Resolve(GetArg(args, "path") ?? "");
        Directory.CreateDirectory(fp);
        return ($"Created folder", true);
    }

    // ===== SEARCH & DISCOVERY =====
    private static (string, bool) ListFiles(Dictionary<string, object> args)
    {
        var fp = Resolve(GetArg(args, "path") ?? ".");
        if (!Directory.Exists(fp)) return ("Dir not found", false);
        var entries = Directory.GetFileSystemEntries(fp).Take(200).OrderBy(x => x);
        return (string.Join("\n", entries.Select(e => Path.GetRelativePath(Ws, e))), true);
    }

    private static (string, bool) FindFile(Dictionary<string, object> args)
    {
        var pattern = GetArg(args, "pattern") ?? "*";
        var fp = Resolve(GetArg(args, "path") ?? ".");
        if (!Directory.Exists(fp)) return ("Dir not found", false);
        var results = Directory.GetFileSystemEntries(fp, pattern, SearchOption.AllDirectories).Take(100);
        return (results.Any() ? string.Join("\n", results.Select(f => Path.GetRelativePath(Ws, f))) : "No matches found.", true);
    }

    private static (string, bool) FindFolder(Dictionary<string, object> args)
    {
        var pattern = GetArg(args, "pattern") ?? "*";
        var fp = Resolve(GetArg(args, "path") ?? ".");
        var results = Directory.GetDirectories(fp, pattern, SearchOption.AllDirectories).Take(100);
        return (results.Any() ? string.Join("\n", results.Select(f => Path.GetRelativePath(Ws, f))) : "No folders found.", true);
    }

    private static (string, bool) SearchInFiles(Dictionary<string, object> args)
    {
        var query = GetArg(args, "query") ?? "";
        var fp = Resolve(GetArg(args, "path") ?? ".");
        var ext = GetArg(args, "extension");
        if (!Directory.Exists(fp)) return ("Dir not found", false);
        var files = string.IsNullOrEmpty(ext) ? Directory.GetFiles(fp, "*.*", SearchOption.AllDirectories) : Directory.GetFiles(fp, $"*.{ext}", SearchOption.AllDirectories);
        var matches = new List<string>();
        int count = 0;
        foreach (var file in files)
        {
            if (count >= 50) break;
            try
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length && count < 50; i++)
                {
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add($"{Path.GetRelativePath(Ws, file)}:{i + 1}: {lines[i].Trim()}");
                        count++;
                    }
                }
            }
            catch { }
        }
        return (matches.Any() ? string.Join("\n", matches) : "No matches.", true);
    }

    private static (string, bool) FileStats(Dictionary<string, object> args)
    {
        var fp = Resolve(GetArg(args, "path") ?? "");
        if (!File.Exists(fp)) return ("Not found", false);
        var info = new FileInfo(fp);
        var lines = File.ReadAllLines(fp).Length;
        var words = File.ReadAllText(fp).Split(' ', '\t', '\n').Length;
        return ($"File: {Path.GetRelativePath(Ws, fp)}\nSize: {info.Length} bytes\nLines: {lines}\nWords: {words}\nModified: {info.LastWriteTime}", true);
    }

    private static (string, bool) DetectLanguage(Dictionary<string, object> args)
    {
        var ext = Path.GetExtension(GetArg(args, "path") ?? "").ToLowerInvariant();
        var lang = ext switch
        {
            ".cs" => "C#", ".py" => "Python", ".js" => "JavaScript", ".ts" => "TypeScript",
            ".jsx" => "JSX", ".tsx" => "TSX", ".html" => "HTML", ".css" => "CSS",
            ".json" => "JSON", ".xml" => "XML", ".md" => "Markdown", ".yaml" or ".yml" => "YAML",
            ".rb" => "Ruby", ".go" => "Go", ".rs" => "Rust", ".java" => "Java",
            ".cpp" or ".c" or ".h" => "C/C++", ".sql" => "SQL", ".sh" => "Shell", ".ps1" => "PowerShell",
            ".txt" => "Plain Text", _ => "Unknown"
        };
        return ($"Language: {lang} (extension: {ext})", true);
    }

    private static (string, bool) CountFiles(Dictionary<string, object> args)
    {
        var fp = Resolve(GetArg(args, "path") ?? ".");
        var ext = GetArg(args, "extension");
        var files = string.IsNullOrEmpty(ext)
            ? Directory.GetFiles(fp, "*.*", SearchOption.AllDirectories)
            : Directory.GetFiles(fp, $"*.{ext}", SearchOption.AllDirectories);
        return ($"Files: {files.Length}", true);
    }

    private static (string, bool) CountTodos(Dictionary<string, object> args)
    {
        var fp = Resolve(GetArg(args, "path") ?? ".");
        var ext = GetArg(args, "extension") ?? "py";
        var files = Directory.GetFiles(fp, $"*.{ext}", SearchOption.AllDirectories);
        var total = 0;
        foreach (var file in files)
        {
            try { total += File.ReadAllLines(file).Count(l => l.Trim().StartsWith("TODO") || l.Trim().StartsWith("FIXME") || l.Trim().StartsWith("HACK")); }
            catch { }
        }
        return ($"TODO/FIXME/HACK count: {total}", true);
    }

    // ===== BULK EDITING =====
    private static (string, bool) ReplaceInFiles(Dictionary<string, object> args)
    {
        var oldText = GetArg(args, "old_text") ?? "";
        var newText = GetArg(args, "new_text") ?? "";
        var ext = GetArg(args, "extension") ?? "*";
        var dryRun = GetBoolArg(args, "dry_run");
        var wholeWord = GetBoolArg(args, "whole_word");
        var fp = Resolve(GetArg(args, "path") ?? ".");
        var files = Directory.GetFiles(fp, $"*.{ext}", SearchOption.AllDirectories);
        var changed = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var content = File.ReadAllText(file);
                var newContent = content;
                if (wholeWord) newContent = Regex.Replace(newContent, $@"\b{Regex.Escape(oldText)}\b", newText);
                else newContent = content.Replace(oldText, newText);
                if (newContent != content)
                {
                    if (!dryRun) { SaveUndo(file); File.WriteAllText(file, newContent); }
                    changed.Add(Path.GetRelativePath(Ws, file));
                }
            }
            catch { }
        }
        var prefix = dryRun ? "[DRY RUN] Would change" : "Changed";
        return ($"{prefix} {changed.Count} files:\n" + string.Join("\n", changed), true);
    }

    // ===== CODE QUALITY =====
    private static (string, bool) LintCheck(Dictionary<string, object> args)
    {
        var fp = Resolve(GetArg(args, "path") ?? "");
        if (!File.Exists(fp)) return ("Not found", false);
        var ext = Path.GetExtension(fp).ToLowerInvariant();
        var issues = new List<string>();
        try
        {
            var lines = File.ReadAllLines(fp);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.EndsWith("\t") || line.EndsWith(" "))
                    issues.Add($"L{i + 1}: Trailing whitespace");
                if (ext == ".py" && line.StartsWith("import *"))
                    issues.Add($"L{i + 1}: Wildcard import");
                if (ext == ".cs" && line.Contains("TODO"))
                    issues.Add($"L{i + 1}: TODO found");
            }
        }
        catch { }
        return (issues.Count == 0 ? "No issues found." : string.Join("\n", issues), true);
    }

    private static (string, bool) CheckFileSyntaxAll(Dictionary<string, object> _)
    {
        var supportedExts = new HashSet<string> { ".py", ".js", ".jsx", ".ts", ".tsx", ".json", ".html", ".htm" };
        var results = new List<string>();
        var numChecked = 0;
        try
        {
            foreach (var file in Directory.GetFiles(Ws, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Ws, file);
                // Skip ignored directories
                var parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                if (parts.Any(p => p is "node_modules" or ".git" or "__pycache__" or ".venv" or "venv" or ".undo_history"))
                    continue;
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!supportedExts.Contains(ext)) continue;
                numChecked++;
                var (result, _) = LintCheck(new() { { "path", rel } });
                if (!result.StartsWith("No issues"))
                    results.Add($"{rel}:\n{result}");
            }
        }
        catch { }
        if (numChecked == 0) return ("No supported files (.py/.js/.ts/.json/.html) found to check.", true);
        if (results.Count == 0) return ($"Checked {numChecked} file(s) -- no issues found.", true);
        return ($"Checked {numChecked} file(s), issues found in {results.Count}:\n\n" + string.Join("\n\n", results), true);
    }

    // ===== DIFF =====
    private static (string, bool) DiffPreview(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path") ?? "";
        var oldText = GetArg(args, "old_text") ?? "";
        var newText = GetArg(args, "new_text") ?? "";
        var oldLines = oldText.Split('\n');
        var newLines = newText.Split('\n');
        var diff = new List<string>();
        int maxLen = Math.Max(oldLines.Length, newLines.Length);
        for (int i = 0; i < maxLen; i++)
        {
            var o = i < oldLines.Length ? oldLines[i] : null;
            var n = i < newLines.Length ? newLines[i] : null;
            if (o == n) diff.Add($"  {o}");
            else if (o == null) diff.Add($"+ {n}");
            else if (n == null) diff.Add($"- {o}");
            else { diff.Add($"- {o}"); diff.Add($"+ {n}"); }
        }
        return ($"Diff preview for {path}:\n" + string.Join("\n", diff), true);
    }

    private static (string, bool) CompareFiles(Dictionary<string, object> args)
    {
        var f1 = Resolve(GetArg(args, "file1") ?? "");
        var f2 = Resolve(GetArg(args, "file2") ?? "");
        if (!File.Exists(f1) || !File.Exists(f2)) return ("One or both files not found.", false);
        var c1 = File.ReadAllText(f1);
        var c2 = File.ReadAllText(f2);
        return (c1 == c2 ? "Files are identical." : $"Files differ. Length1={c1.Length}, Length2={c2.Length}", true);
    }

    // ===== GIT =====
    private static (string, bool) GitStatus(Dictionary<string, object> args) => RunGit(GetArg(args, "path"), "status --short");
    private static (string, bool) GitDiff(Dictionary<string, object> args) => RunGit(GetArg(args, "path"), "diff");
    private static (string, bool) GitLog(Dictionary<string, object> args) => RunGit(GetArg(args, "path"), "log --oneline -10");
    private static (string, bool) GitCommit(Dictionary<string, object> args)
    {
        var msg = GetArg(args, "message") ?? "Update";
        return RunGit(GetArg(args, "path"), $"add -A && git commit -m \"{msg}\"");
    }
    private static async Task<(string, bool)> GitCloneAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var url = GetArg(args, "url") ?? "";
        var path = GetArg(args, "path") ?? ".";
        if (string.IsNullOrEmpty(url)) return ("Missing url", false);
        return await RunCommandAsync(new() { { "command", $"git clone {url} {Resolve(path)}" } }, ct);
    }

    private static async Task<(string, bool)> GitFetcherAsync(Dictionary<string, object> args)
    {
        var repo = GetArg(args, "repo") ?? "";
        var includeTree = GetBoolArg(args, "include_tree");
        if (string.IsNullOrEmpty(repo)) return ("Missing repo", false);

        // Parse "owner/name" or full GitHub URL
        var match = Regex.Match(repo, @"github\.com/([^/]+)/([^/]+?)(?:\.git)?/?$");
        string owner, name;
        if (match.Success)
        {
            owner = match.Groups[1].Value;
            name = match.Groups[2].Value;
        }
        else if (repo.Contains("/") && !repo.StartsWith("http"))
        {
            var parts = repo.Split('/');
            owner = parts[0];
            name = parts[1];
        }
        else
        {
            return ($"Could not parse '{repo}' as a GitHub repo -- use 'owner/name' or a full github.com URL.", false);
        }

        try
        {
            var apiUrl = $"https://api.github.com/repos/{owner}/{name}";
            var resp = await _http.GetStringAsync(apiUrl);
            var data = JObject.Parse(resp);

            if (data["message"]?.ToString() == "Not Found")
                return ($"Repository '{owner}/{name}' not found (private, or doesn't exist).", false);
            if ((data["message"]?.ToString() ?? "").Contains("rate limit"))
                return ("GitHub API rate limit exceeded. Try again later.", false);

            var licenseName = data["license"]?["name"]?.ToString() ?? "None";
            var lines = new List<string>
            {
                $"{data["full_name"]?.ToString() ?? $"{owner}/{name}"}",
                $"Description: {data["description"]?.ToString() ?? "(none)"}",
                $"Stars: {data["stargazers_count"]}  Forks: {data["forks_count"]}  Watchers: {data["watchers_count"]}  Open issues: {data["open_issues_count"]}",
                $"License: {licenseName}",
                $"Primary language: {data["language"]?.ToString() ?? "(unknown)"}",
                $"Default branch: {data["default_branch"]?.ToString() ?? "main"}",
                $"Last push: {data["pushed_at"]?.ToString() ?? "unknown"}",
                $"Archived: {data["archived"]}",
                $"URL: {data["html_url"]?.ToString() ?? $"https://github.com/{owner}/{name}"}"
            };

            if (includeTree)
            {
                try
                {
                    var treeUrl = $"https://api.github.com/repos/{owner}/{name}/contents/";
                    var treeResp = await _http.GetStringAsync(treeUrl);
                    var entries = JArray.Parse(treeResp);
                    lines.Add("");
                    lines.Add("Top-level contents:");
                    foreach (var entry in entries.OrderBy(e => e["type"]?.ToString() != "dir").ThenBy(e => e["name"]?.ToString()))
                    {
                        var icon = entry["type"]?.ToString() == "dir" ? "[DIR]" : "[FILE]";
                        lines.Add($"  {icon} {entry["name"]}");
                    }
                }
                catch { lines.Add(""); lines.Add("(Could not fetch file tree)"); }
            }

            return (string.Join("\n", lines), true);
        }
        catch (HttpRequestException ex)
        {
            return ($"Failed to fetch repo info: {ex.Message}", false);
        }
        catch (Exception ex)
        {
            return ($"Failed to parse GitHub response: {ex.Message}", false);
        }
    }

    // ===== EXECUTION =====
    private static async Task<(string, bool)> RunCommandAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var cmd = GetArg(args, "command") ?? "";
        if (string.IsNullOrEmpty(cmd)) return ("No command", false);
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = $"/c {cmd}",
                WorkingDirectory = Ws, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            p.Start();
            var out1 = await p.StandardOutput.ReadToEndAsync(ct);
            var err1 = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var result = string.IsNullOrEmpty(out1) ? err1 : out1;
            return (result.Length > 10000 ? result[..10000] + "..." : result, p.ExitCode == 0);
        }
        catch (Exception ex) { return ($"Failed: {ex.Message}", false); }
    }

    // ===== BACKGROUND PROCESSES =====
    private static void LoadBgRegistry()
    {
        try
        {
            if (File.Exists(_bgRegistryFile))
            {
                var json = File.ReadAllText(_bgRegistryFile);
                var reg = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, object>>>(json);
                if (reg != null)
                {
                    foreach (var kv in reg)
                    {
                        var pid = int.Parse(kv.Key);
                        if (IsPidAlive(pid))
                        {
                            _bgProcesses[pid] = new ProcessInfo
                            {
                                Pid = pid,
                                Command = kv.Value["command"]?.ToString() ?? "",
                                LogPath = kv.Value["log_file"]?.ToString() ?? "",
                                Started = DateTime.Now
                            };
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static void SaveBgRegistry()
    {
        try
        {
            var reg = new Dictionary<string, Dictionary<string, object>>();
            foreach (var kv in _bgProcesses)
            {
                reg[kv.Key.ToString()] = new Dictionary<string, object>
                {
                    ["command"] = kv.Value.Command,
                    ["log_file"] = Path.GetRelativePath(Ws, kv.Value.LogPath),
                    ["started"] = kv.Value.Started.ToString("o")
                };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_bgRegistryFile)!);
            File.WriteAllText(_bgRegistryFile, JsonConvert.SerializeObject(reg, Formatting.Indented));
        }
        catch { }
    }

    private static bool IsPidAlive(int pid)
    {
        try { return Process.GetProcessById(pid) != null; }
        catch { return false; }
    }

    private static async Task<(string, bool)> StartBackgroundProcessAsync(Dictionary<string, object> args)
    {
        var command = GetArg(args, "command") ?? "";
        var workingDir = GetArg(args, "working_dir") ?? ".";
        if (string.IsNullOrEmpty(command)) return ("No command provided.", false);

        var dir = Resolve(workingDir);
        if (!Directory.Exists(dir)) return ($"Working directory not found: {workingDir}", false);

        Directory.CreateDirectory(_bgLogDir);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var logPath = Path.Combine(_bgLogDir, $"{ts}.log");

        try
        {
            var logFile = File.Create(logPath);
            logFile.Close();

            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = $"/c {command}",
                WorkingDirectory = dir,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };

            // Redirect output to log file
            var logStream = new StreamWriter(logPath, append: true) { AutoFlush = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) logStream.WriteLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) logStream.WriteLine(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            _bgProcesses[p.Id] = new ProcessInfo
            {
                Pid = p.Id,
                Command = command,
                LogPath = logPath,
                Started = DateTime.Now
            };
            SaveBgRegistry();

            // Detach the log stream so it keeps writing
            _ = Task.Run(async () =>
            {
                try { await p.WaitForExitAsync(); }
                catch { }
                try { logStream.Dispose(); }
                catch { }
            });

            return ($"Started background process PID {p.Id}: {command}\nLog: {Path.GetRelativePath(Ws, logPath)}", true);
        }
        catch (Exception ex) { return ($"Failed to start background process: {ex.Message}", false); }
    }

    private static (string, bool) ListBackgroundProcesses()
    {
        if (_bgProcesses.Count == 0) return ("No background processes have been started.", true);
        var lines = new List<string>();
        foreach (var kv in _bgProcesses)
        {
            var alive = IsPidAlive(kv.Key);
            var status = alive ? "[RUNNING]" : "[STOPPED]";
            lines.Add($"PID {kv.Key} [{status}]: {kv.Value.Command}  (log: {Path.GetRelativePath(Ws, kv.Value.LogPath)})");
        }
        return (string.Join("\n", lines), true);
    }

    private static (string, bool) ReadBackgroundLog(Dictionary<string, object> args)
    {
        var pidStr = GetArg(args, "pid");
        if (!int.TryParse(pidStr, out var pid)) return ($"Invalid PID: {pidStr}", false);
        var tailLines = 50;
        var tailStr = GetArg(args, "tail_lines");
        if (tailStr != null) int.TryParse(tailStr, out tailLines);

        if (!_bgProcesses.TryGetValue(pid, out var info))
            return ($"No known background process with PID {pid}.", false);

        if (!File.Exists(info.LogPath))
            return ($"Log file missing for PID {pid}.", false);

        var allLines = File.ReadAllLines(info.LogPath);
        var tail = allLines.Skip(Math.Max(0, allLines.Length - tailLines)).ToArray();
        var alive = IsPidAlive(pid);
        var status = alive ? "still running" : "process has stopped";
        return ($"Status: {status}\n--- last {tail.Length} line(s) of output ---\n" + string.Join("\n", tail), true);
    }

    private static (string, bool) StopBackgroundProcess(Dictionary<string, object> args)
    {
        var pidStr = GetArg(args, "pid");
        if (!int.TryParse(pidStr, out var pid)) return ($"Invalid PID: {pidStr}", false);

        if (!IsPidAlive(pid))
            return ($"PID {pid} is not running (already stopped).", true);

        try
        {
            var proc = Process.GetProcessById(pid);
            proc.Kill();
            proc.WaitForExit(3000);
            return ($"Stopped background process PID {pid}.", true);
        }
        catch (Exception ex) { return ($"Failed to stop PID {pid}: {ex.Message}", false); }
    }

    private static async Task<(string, bool)> WaitProcessAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var pidStr = GetArg(args, "pid");
        if (!int.TryParse(pidStr, out var pid)) return ($"Invalid PID: {pidStr}", false);
        var timeout = 60;
        var timeoutStr = GetArg(args, "timeout");
        if (timeoutStr != null) int.TryParse(timeoutStr, out timeout);
        var pollInterval = 1.0;
        var pollStr = GetArg(args, "poll_interval");
        if (pollStr != null) double.TryParse(pollStr, out pollInterval);

        if (!_bgProcesses.ContainsKey(pid))
            return ($"No known background process with PID {pid}.", false);

        if (!IsPidAlive(pid))
            return ($"PID {pid} has already finished (nothing to wait for).", true);

        var waited = 0.0;
        while (waited < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPidAlive(pid))
            {
                var info = _bgProcesses.GetValueOrDefault(pid);
                var logHint = info != null ? $" (log: {Path.GetRelativePath(Ws, info.LogPath)})" : "";
                return ($"PID {pid} finished after ~{waited:F1}s.{logHint} Use read_background_log to see its output.", true);
            }
            await Task.Delay((int)(pollInterval * 1000), ct);
            waited += pollInterval;
        }

        return ($"Timed out after {timeout}s -- PID {pid} is still running. Call wait_process again to keep waiting, or use read_background_log to check progress.", false);
    }

    // ===== ZIP =====
    private static (string, bool) ZipWorkspace(Dictionary<string, object> args)
    {
        var output = GetArg(args, "output_path") ?? "workspace.zip";
        var fp = Resolve(output);
        if (File.Exists(fp)) File.Delete(fp);
        ZipFile.CreateFromDirectory(Ws, fp);
        return ($"Zipped to: {output}", true);
    }

    private static (string, bool) CreateZip(Dictionary<string, object> args)
    {
        var src = Resolve(GetArg(args, "source") ?? ".");
        var output = Resolve(GetArg(args, "output_path") ?? "archive.zip");
        if (File.Exists(output)) File.Delete(output);
        if (Directory.Exists(src)) ZipFile.CreateFromDirectory(src, output);
        else return ("Source not found", false);
        return ($"Created: {output}", true);
    }

    private static (string, bool) ExtractZip(Dictionary<string, object> args)
    {
        var archive = Resolve(GetArg(args, "archive_path") ?? "");
        var dest = Resolve(GetArg(args, "destination") ?? ".");
        if (!File.Exists(archive)) return ("Archive not found", false);
        Directory.CreateDirectory(dest);
        ZipFile.ExtractToDirectory(archive, dest, overwriteFiles: true);
        return ("Extracted", true);
    }

    // ===== UNDO =====
    private static (string, bool) UndoLastChange()
    {
        if (_undoStack.Count == 0) return ("Nothing to undo", false);
        var entry = _undoStack.Last();
        if (File.Exists(entry.FilePath))
        {
            File.WriteAllText(entry.FilePath, entry.OldContent);
            _undoStack.RemoveAt(_undoStack.Count - 1);
            return ($"Undone change to: {entry.FilePath}", true);
        }
        _undoStack.RemoveAt(_undoStack.Count - 1);
        return ("File no longer exists, undo skipped", false);
    }

    // ===== SYSTEM =====
    private static (string, bool) ListActiveWindows() => ("Window listing requires system access enabled.", true);
    private static (string, bool) ListSystemProcesses()
    {
        var lines = new List<string> { "PID\tCPU\tMemory\tName" };
        try
        {
            foreach (var p in Process.GetProcesses().OrderByDescending(p => p.WorkingSet64).Take(30))
                lines.Add($"{p.Id}\t{p.TotalProcessorTime.TotalSeconds:F1}s\t{p.WorkingSet64 / 1024 / 1024}MB\t{p.ProcessName}");
        }
        catch { }
        return (string.Join("\n", lines), true);
    }

    // ===== NETWORKING =====
    private static (string, bool) CheckPortInUse(Dictionary<string, object> args)
    {
        var port = GetArg(args, "port") ?? "8080";
        try
        {
            var listener = System.Net.Sockets.TcpListener.Create(int.Parse(port));
            listener.Start();
            listener.Stop();
            return ($"Port {port} is available.", true);
        }
        catch { return ($"Port {port} is in use.", true); }
    }

    private static async Task<(string, bool)> HttpRequestAsync(Dictionary<string, object> args)
    {
        var url = GetArg(args, "url") ?? "";
        var method = (GetArg(args, "method") ?? "GET").ToUpperInvariant();
        var timeoutSec = 15;
        var timeoutStr = GetArg(args, "timeout");
        if (timeoutStr != null) int.TryParse(timeoutStr, out timeoutSec);

        if (string.IsNullOrEmpty(url)) return ("No URL provided.", false);

        // Parse optional headers
        var headers = new Dictionary<string, string>();
        if (args.TryGetValue("headers", out var headersObj) && headersObj is JObject hdrJo)
            foreach (var prop in hdrJo.Properties())
                headers[prop.Name] = prop.Value.ToString();

        var body = GetArg(args, "body");

        using var cts = new CancellationTokenSource(timeoutSec * 1000);
        try
        {
            var req = new HttpRequestMessage(new System.Net.Http.HttpMethod(method), url);
            foreach (var h in headers)
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (body != null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req, cts.Token);
            var respBody = await resp.Content.ReadAsStringAsync();
            if (respBody.Length > 3000)
                respBody = respBody[..3000] + $"\n... (truncated, {respBody.Length} total chars)";

            var headerLines = string.Join("\n", resp.Headers.Select(h => $"  {h.Key}: {string.Join(", ", h.Value)}"));
            var result = $"{method} {url}\nStatus: {(int)resp.StatusCode} {resp.ReasonPhrase}\nHeaders:\n{headerLines}\n\nBody:\n{respBody}";
            return (result, true);
        }
        catch (TaskCanceledException) { return ("Request timed out.", false); }
        catch (HttpRequestException ex) { return ($"Request failed: {ex.Message}", false); }
        catch (Exception ex) { return ($"Request failed: {ex.Message}", false); }
    }

    // ===== ENVIRONMENT =====
    private static (string, bool) EnvVarCheck(Dictionary<string, object> args)
    {
        var name = GetArg(args, "name") ?? "";
        if (string.IsNullOrEmpty(name)) return ("Provide a variable name", false);
        var val = Environment.GetEnvironmentVariable(name);
        return (string.IsNullOrEmpty(val) ? $"{name} is not set." : $"{name}={val}", true);
    }

    // ===== IMAGE TOOLS =====
    private static async Task<(string, bool)> ImageFetchAsync(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path") ?? "";
        var question = GetArg(args, "question") ?? "Describe this image in detail.";
        if (string.IsNullOrEmpty(path)) return ("No image path provided.", false);

        var paths = path.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        var supportedExts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };
        var mimeMap = new Dictionary<string, string>
        {
            [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp", [".gif"] = "image/gif", [".bmp"] = "image/bmp"
        };

        var parts = new JArray();
        parts.Add(new JObject { ["text"] = question });

        foreach (var relPath in paths)
        {
            var fp = Resolve(relPath);
            if (!File.Exists(fp)) return ($"Image not found: {relPath}", false);
            var ext = Path.GetExtension(fp).ToLowerInvariant();
            if (!supportedExts.Contains(ext))
                return ($"Unsupported image format '{ext}' for {relPath}. Supported: {string.Join(", ", supportedExts)}", false);
            var bytes = File.ReadAllBytes(fp);
            parts.Add(new JObject
            {
                ["inline_data"] = new JObject
                {
                    ["mime_type"] = mimeMap[ext],
                    ["data"] = Convert.ToBase64String(bytes)
                }
            });
        }

        var apiKey = ConfigManager.GeminiApiKey;
        if (string.IsNullOrEmpty(apiKey)) return ("No Gemini API key configured.", false);

        try
        {
            var requestBody = new JObject
            {
                ["contents"] = new JArray { new JObject { ["parts"] = parts } }
            };
            var model = "gemini-2.5-flash";
            var resp = await _http.PostAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}",
                new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json"));
            var json = await resp.Content.ReadAsStringAsync();
            var result = JObject.Parse(json);
            var text = result["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
            return (text ?? "(The model didn't return a text description.)", true);
        }
        catch (Exception ex) { return ($"Failed to analyze image(s): {ex.Message}", false); }
    }

    private static async Task<(string, bool)> ImageFetchPuterAsync(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path") ?? "";
        var question = GetArg(args, "question") ?? "Describe this image in detail.";
        if (string.IsNullOrEmpty(path)) return ("No image path provided.", false);

        var paths = path.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (paths.Count > 1) return ("Image_Fetch_Puter supports one image at a time.", false);

        var supportedExts = new HashSet<string> { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };
        var mimeMap = new Dictionary<string, string>
        {
            [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp", [".gif"] = "image/gif", [".bmp"] = "image/bmp"
        };

        var fp = Resolve(paths[0]);
        if (!File.Exists(fp)) return ($"Image not found: {paths[0]}", false);
        var ext = Path.GetExtension(fp).ToLowerInvariant();
        if (!supportedExts.Contains(ext))
            return ($"Unsupported image format '{ext}'.", false);

        var puterToken = ConfigManager.LoadPuterToken();
        if (string.IsNullOrEmpty(puterToken)) return ("No Puter.js token configured.", false);

        var bytes = File.ReadAllBytes(fp);
        var base64 = Convert.ToBase64String(bytes);
        var visionModel = ConfigManager.Settings.PuterVisionModel;

        try
        {
            var requestBody = new JObject
            {
                ["model"] = visionModel,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JObject { ["url"] = $"data:{mimeMap[ext]};base64,{base64}" }
                            },
                            new JObject { ["type"] = "text", ["text"] = question }
                        }
                    }
                },
                ["max_tokens"] = 4096
            };
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.puter.com/puterai/openai/v1/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", puterToken);
            req.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            var result = JObject.Parse(json);
            var text = result["choices"]?[0]?["message"]?["content"]?.ToString();
            return (text ?? "(The Puter.js model didn't return a text description.)", true);
        }
        catch (Exception ex) { return ($"Puter.js vision request failed: {ex.Message}", false); }
    }

    private static async Task<(string, bool)> ImageCreateAsync(Dictionary<string, object> args)
    {
        var prompt = GetArg(args, "prompt") ?? "";
        var outputPath = GetArg(args, "output_path") ?? "generated.png";
        var aspectRatio = GetArg(args, "aspect_ratio") ?? "1:1";

        var validRatios = new HashSet<string> { "1:1", "16:9", "9:16", "4:3", "3:2" };
        if (!validRatios.Contains(aspectRatio))
            return ($"Invalid aspect_ratio '{aspectRatio}'. Must be one of: {string.Join(", ", validRatios)}", false);

        var outFp = Resolve(outputPath);
        if (!Path.GetExtension(outFp).Equals(".png", StringComparison.OrdinalIgnoreCase))
            return ("output_path must end in .png", false);

        var apiKey = ConfigManager.GeminiApiKey;
        if (string.IsNullOrEmpty(apiKey)) return ("No Gemini API key configured.", false);

        string? imageBytesBase64 = null;
        Exception? lastError = null;

        foreach (var modelName in ConfigManager.ImageModelChain)
        {
            try
            {
                var requestBody = new JObject
                {
                    ["contents"] = new JArray { new JObject { ["parts"] = new JArray { new JObject { ["text"] = prompt } } } },
                    ["generationConfig"] = new JObject
                    {
                        ["responseModalities"] = new JArray { "Text", "Image" },
                        ["imageConfig"] = new JObject { ["aspectRatio"] = aspectRatio }
                    }
                };
                var resp = await _http.PostAsync(
                    $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}",
                    new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json"));
                var json = await resp.Content.ReadAsStringAsync();
                var result = JObject.Parse(json);

                var candidates = result["candidates"] as JArray;
                if (candidates != null)
                {
                    foreach (var candidate in candidates)
                    {
                        var parts = candidate?["content"]?["parts"] as JArray;
                        if (parts != null)
                        {
                            foreach (var part in parts)
                            {
                                var data = part?["inlineData"]?["data"]?.ToString();
                                if (!string.IsNullOrEmpty(data))
                                {
                                    imageBytesBase64 = data;
                                    break;
                                }
                            }
                        }
                        if (imageBytesBase64 != null) break;
                    }
                }
                if (imageBytesBase64 != null) break;
            }
            catch (Exception ex) { lastError = ex; }
        }

        if (imageBytesBase64 == null)
            return ($"Failed to generate image (all models failed): {lastError?.Message}", false);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outFp)!);
            SaveUndo(outFp);
            File.WriteAllBytes(outFp, Convert.FromBase64String(imageBytesBase64));
            return ($"Image generated and saved: {outputPath} ({Convert.FromBase64String(imageBytesBase64).Length} bytes)", true);
        }
        catch (Exception ex) { return ($"Failed to save image: {ex.Message}", false); }
    }

    private static async Task<(string, bool)> ImageCreatePuterAsync(Dictionary<string, object> args)
    {
        var prompt = GetArg(args, "prompt") ?? "";
        var outputPath = GetArg(args, "output_path") ?? "generated.png";

        var outFp = Resolve(outputPath);
        if (!Path.GetExtension(outFp).Equals(".png", StringComparison.OrdinalIgnoreCase))
            return ("output_path must end in .png", false);

        var puterToken = ConfigManager.LoadPuterToken();
        if (string.IsNullOrEmpty(puterToken)) return ("No Puter.js token configured.", false);

        var imageGenModel = ConfigManager.Settings.PuterImageGenModel;

        try
        {
            var requestBody = new JObject
            {
                ["model"] = imageGenModel,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = prompt
                    }
                },
                ["max_tokens"] = 4096
            };
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.puter.com/puterai/openai/v1/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", puterToken);
            req.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();

            // Puter.js image generation via REST is experimental/unverified.
            // The response is unlikely to contain image data in this format.
            var result = JObject.Parse(json);
            var content = result["choices"]?[0]?["message"]?["content"]?.ToString();

            if (content != null && content.StartsWith("data:image"))
            {
                // Extract base64 data from data URL
                var base64Data = content.Substring(content.IndexOf(",") + 1);
                Directory.CreateDirectory(Path.GetDirectoryName(outFp)!);
                File.WriteAllBytes(outFp, Convert.FromBase64String(base64Data));
                return ($"Image generated via Puter.js and saved: {outputPath}", true);
            }

            return ($"Puter.js image generation returned text but no image data. This endpoint is experimental.\nResponse: {(content ?? json).Substring(0, Math.Min(500, (content ?? json).Length))}", false);
        }
        catch (Exception ex)
        {
            return ($"Puter.js image generation failed: {ex.Message}\nNote: Puter.js image generation via REST is experimental and may not work.", false);
        }
    }

    // ===== SCREEN TOOLS =====
    private static async Task<(string, bool)> CaptureScreenshotAsync(string savePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        var psScript = $@"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$b = New-Object System.Drawing.Bitmap([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($b)
$g.CopyFromScreen([System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Location, [System.Drawing.Point]::Empty, [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Size)
$b.Save('{savePath.Replace("'", "''")}')
$g.Dispose()
$b.Dispose()
";
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = string.Concat("-NoProfile -NonInteractive -Command \"", psScript.Replace("\"", "'"), "\""),
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            p.Start();
            await p.WaitForExitAsync();
            return File.Exists(savePath)
                ? (savePath, true)
                : ($"Screenshot failed. No graphical display available?", false);
        }
        catch (Exception ex) { return ($"Could not capture screen: {ex.Message}", false); }
    }

    private static async Task<(string, bool)> ViewScreenAsync(Dictionary<string, object> args)
    {
        var question = GetArg(args, "question") ?? "Describe what's currently on screen.";
        var onlyIfChanged = GetBoolArg(args, "only_if_changed");
        var changeThreshold = 2.0;
        var thresholdStr = GetArg(args, "change_threshold");
        if (thresholdStr != null) double.TryParse(thresholdStr, out changeThreshold);

        var screenshotPath = Path.Combine(_screenshotDir, $"screen_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png");
        var (captureResult, captureOk) = await CaptureScreenshotAsync(screenshotPath);
        if (!captureOk) return (captureResult, false);

        // Skip analysis if screen hasn't changed
        if (onlyIfChanged && _lastScreenshotPath != null && File.Exists(_lastScreenshotPath))
        {
            try
            {
                var oldBytes = File.ReadAllBytes(_lastScreenshotPath);
                var newBytes = File.ReadAllBytes(screenshotPath);
                if (oldBytes.Length == newBytes.Length)
                {
                    int diffCount = 0;
                    for (int i = 0; i < oldBytes.Length; i += 3) // sample every 3rd byte (approx pixel)
                    {
                        if (Math.Abs(oldBytes[i] - newBytes[i]) > 10) diffCount++;
                    }
                    var pctChanged = (double)diffCount / (oldBytes.Length / 3) * 100.0;
                    if (pctChanged < changeThreshold)
                    {
                        _lastScreenshotPath = screenshotPath;
                        return ($"Screen looks essentially unchanged (~{pctChanged:F1}% differ, threshold {changeThreshold}%). Call with only_if_changed=false to force analysis.", true);
                    }
                }
            }
            catch { }
        }

        _lastScreenshotPath = screenshotPath;

        var apiKey = ConfigManager.GeminiApiKey;
        if (string.IsNullOrEmpty(apiKey)) return ("No Gemini API key configured for screen analysis.", false);

        try
        {
            var bytes = File.ReadAllBytes(screenshotPath);
            var requestBody = new JObject
            {
                ["contents"] = new JArray
                {
                    new JObject
                    {
                        ["parts"] = new JArray
                        {
                            new JObject { ["text"] = question },
                            new JObject
                            {
                                ["inline_data"] = new JObject
                                {
                                    ["mime_type"] = "image/png",
                                    ["data"] = Convert.ToBase64String(bytes)
                                }
                            }
                        }
                    }
                }
            };
            var resp = await _http.PostAsync(
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=" + apiKey,
                new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json"));
            var json = await resp.Content.ReadAsStringAsync();
            var result = JObject.Parse(json);
            var text = result["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
            return (text ?? "(The model didn't return a description.)", true);
        }
        catch (Exception ex) { return ($"Failed to analyze screenshot: {ex.Message}", false); }
    }

    private static async Task<(string, bool)> ViewScreenPuterAsync(Dictionary<string, object> args)
    {
        var question = GetArg(args, "question") ?? "Describe what's currently on screen.";

        var puterToken = ConfigManager.LoadPuterToken();
        if (string.IsNullOrEmpty(puterToken)) return ("No Puter.js token configured.", false);

        var screenshotPath = Path.Combine(_screenshotDir, $"screen_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png");
        var (captureResult, captureOk) = await CaptureScreenshotAsync(screenshotPath);
        if (!captureOk) return (captureResult, false);

        _lastScreenshotPath = screenshotPath;

        var bytes = File.ReadAllBytes(screenshotPath);
        var base64 = Convert.ToBase64String(bytes);
        var visionModel = ConfigManager.Settings.PuterVisionModel;

        try
        {
            var requestBody = new JObject
            {
                ["model"] = visionModel,
                ["messages"] = new JArray
                {
                    new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JObject { ["url"] = $"data:image/png;base64,{base64}" }
                            },
                            new JObject { ["type"] = "text", ["text"] = question }
                        }
                    }
                },
                ["max_tokens"] = 4096
            };
            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.puter.com/puterai/openai/v1/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", puterToken);
            req.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            var result = JObject.Parse(json);
            var text = result["choices"]?[0]?["message"]?["content"]?.ToString();
            return (text ?? "(The Puter.js model didn't return a description.)", true);
        }
        catch (Exception ex) { return ($"Puter.js vision request failed: {ex.Message}", false); }
    }

    private static async Task<(string, bool)> WatchScreenAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var question = GetArg(args, "question") ?? "Describe what's currently on screen, and note anything new or changed.";
        var durationSec = 30;
        var intervalSec = 5;
        var changeThreshold = 2.0;
        var usePuter = GetBoolArg(args, "use_puter");

        if (GetArg(args, "duration_seconds") is string ds) int.TryParse(ds, out durationSec);
        if (GetArg(args, "interval_seconds") is string is2) int.TryParse(is2, out intervalSec);
        if (GetArg(args, "change_threshold") is string ts) double.TryParse(ts, out changeThreshold);

        durationSec = Math.Clamp(durationSec, 5, 600);
        intervalSec = Math.Max(1, intervalSec);

        Directory.CreateDirectory(_screenshotDir);
        var logLines = new List<string>();
        byte[]? previousBytes = null;
        var start = DateTime.UtcNow;
        var frameCount = 0;
        var analyzedCount = 0;

        while ((DateTime.UtcNow - start).TotalSeconds < durationSec)
        {
            ct.ThrowIfCancellationRequested();
            var screenshotPath = Path.Combine(_screenshotDir, $"watch_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.png");
            var (captureResult, captureOk) = await CaptureScreenshotAsync(screenshotPath);
            if (!captureOk) { logLines.Add($"[error] could not capture screen: {captureResult}"); break; }

            frameCount++;
            var elapsed = Math.Round((DateTime.UtcNow - start).TotalSeconds, 1);
            var currentBytes = File.ReadAllBytes(screenshotPath);

            var changed = true;
            var changedPct = 100.0;
            if (previousBytes != null && previousBytes.Length == currentBytes.Length)
            {
                int diffCount = 0;
                for (int i = 0; i < previousBytes.Length; i += 3)
                {
                    if (Math.Abs(previousBytes[i] - currentBytes[i]) > 10) diffCount++;
                }
                changedPct = (double)diffCount / (previousBytes.Length / 3) * 100.0;
                changed = changedPct >= changeThreshold;
            }

            if (changed)
            {
                try
                {
                    string? desc;
                    if (usePuter)
                    {
                        var puterToken = ConfigManager.LoadPuterToken();
                        if (string.IsNullOrEmpty(puterToken)) { logLines.Add("[error] No Puter.js token configured."); break; }

                        var base64 = Convert.ToBase64String(currentBytes);
                        var requestBody = new JObject
                        {
                            ["model"] = ConfigManager.Settings.PuterVisionModel,
                            ["messages"] = new JArray
                            {
                                new JObject
                                {
                                    ["role"] = "user",
                                    ["content"] = new JArray
                                    {
                                        new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = $"data:image/png;base64,{base64}" } },
                                        new JObject { ["type"] = "text", ["text"] = question }
                                    }
                                }
                            },
                            ["max_tokens"] = 4096
                        };
                        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.puter.com/puterai/openai/v1/chat/completions");
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", puterToken);
                        req.Content = new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json");
                        var resp = await _http.SendAsync(req);
                        var json = await resp.Content.ReadAsStringAsync();
                        desc = JObject.Parse(json)["choices"]?[0]?["message"]?["content"]?.ToString();
                    }
                    else
                    {
                        var apiKey = ConfigManager.GeminiApiKey;
                        if (string.IsNullOrEmpty(apiKey)) { logLines.Add("[error] No Gemini API key configured."); break; }

                        var requestBody = new JObject
                        {
                            ["contents"] = new JArray
                            {
                                new JObject
                                {
                                    ["parts"] = new JArray
                                    {
                                        new JObject { ["text"] = question },
                                        new JObject { ["inline_data"] = new JObject { ["mime_type"] = "image/png", ["data"] = Convert.ToBase64String(currentBytes) } }
                                    }
                                }
                            }
                        };
                        var resp = await _http.PostAsync(
                            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key=" + apiKey,
                            new StringContent(requestBody.ToString(), Encoding.UTF8, "application/json"));
                        var json = await resp.Content.ReadAsStringAsync();
                        desc = JObject.Parse(json)["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.ToString();
                    }
                    analyzedCount++;
                    logLines.Add($"[t={elapsed}s, {changedPct:F1}% changed] {desc ?? "(no description)"}");
                }
                catch (Exception ex) { logLines.Add($"[t={elapsed}s] analysis failed: {ex.Message}"); }
            }
            previousBytes = currentBytes;

            var waitMs = Math.Max(0, intervalSec * 1000 - (int)((DateTime.UtcNow - start).TotalMilliseconds - elapsed * 1000));
            if (waitMs > 0) await Task.Delay(waitMs, ct);
        }

        var totalSec = Math.Round((DateTime.UtcNow - start).TotalSeconds, 1);
        if (logLines.Count == 0)
            return ($"Watched screen for {totalSec}s ({frameCount} frames) -- no meaningful changes detected (threshold {changeThreshold}%).", true);

        var header = $"Screen watch log -- {totalSec}s, {frameCount} frames, {analyzedCount} analyzed (via {(usePuter ? "Puter.js" : "Gemini")}):\n";
        return (header + string.Join("\n", logLines), true);
    }

    // ===== PROJECT TOOLS =====
    private static (string, bool) ListDependencies()
    {
        var sections = new List<string>();
        var foundAny = false;

        var pkgJson = Path.Combine(Ws, "package.json");
        if (File.Exists(pkgJson))
        {
            foundAny = true;
            try
            {
                var data = JObject.Parse(File.ReadAllText(pkgJson));
                var deps = data["dependencies"];
                var devDeps = data["devDependencies"];
                var lines = new List<string> { "package.json (Node.js):" };
                if (deps != null && deps.HasValues)
                {
                    lines.Add("  dependencies:");
                    foreach (var prop in deps.Cast<JProperty>())
                        lines.Add($"    {prop.Name}: {prop.Value}");
                }
                if (devDeps != null && devDeps.HasValues)
                {
                    lines.Add("  devDependencies:");
                    foreach (var prop in devDeps.Cast<JProperty>())
                        lines.Add($"    {prop.Name}: {prop.Value}");
                }
                if ((deps == null || !deps.HasValues) && (devDeps == null || !devDeps.HasValues))
                    lines.Add("  (no dependencies declared)");
                sections.Add(string.Join("\n", lines));
            }
            catch { sections.Add("package.json exists but is not valid JSON."); }
        }

        var reqTxt = Path.Combine(Ws, "requirements.txt");
        if (File.Exists(reqTxt))
        {
            foundAny = true;
            var lines = File.ReadAllLines(reqTxt)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
                .ToList();
            var section = new List<string> { "requirements.txt (Python):" };
            if (lines.Count > 0) section.AddRange(lines.Select(l => $"  {l}"));
            else section.Add("  (empty)");
            sections.Add(string.Join("\n", section));
        }

        if (File.Exists(Path.Combine(Ws, "pyproject.toml")))
            { foundAny = true; sections.Add("pyproject.toml exists (Python). Use read_file to inspect."); }
        if (File.Exists(Path.Combine(Ws, "Cargo.toml")))
            { foundAny = true; sections.Add("Cargo.toml exists (Rust). Use read_file to inspect."); }
        if (File.Exists(Path.Combine(Ws, "go.mod")))
            { foundAny = true; sections.Add("go.mod exists (Go). Use read_file to inspect."); }

        if (!foundAny) return ("No recognized dependency manifest found (package.json, requirements.txt, pyproject.toml, Cargo.toml, go.mod).", true);
        return (string.Join("\n\n", sections), true);
    }

    private static async Task<(string, bool)> AddDependencyAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var package = GetArg(args, "package") ?? "";
        var dev = GetBoolArg(args, "dev");
        var version = GetArg(args, "version");
        if (string.IsNullOrEmpty(package)) return ("No package name provided.", false);

        var pkgJson = Path.Combine(Ws, "package.json");
        var reqTxt = Path.Combine(Ws, "requirements.txt");

        if (File.Exists(pkgJson))
        {
            var pkgSpec = string.IsNullOrEmpty(version) ? package : $"{package}@{version}";
            var cmd = $"npm install {(dev ? "--save-dev" : "--save")} {pkgSpec}";
            return await RunCommandAsync(new() { { "command", cmd } }, ct);
        }

        // Default to Python/requirements.txt
        var line = string.IsNullOrEmpty(version) ? package : $"{package}{version}";
        var existing = File.Exists(reqTxt) ? File.ReadAllText(reqTxt) : "";
        var pkgName = line.Split("==")[0].Split(">=")[0].Trim();
        if (existing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Any(l => l.Trim().Split("==")[0].Split(">=")[0].Trim() == pkgName))
            return ($"'{package}' already appears in requirements.txt.", true);

        SaveUndo(reqTxt);
        var newContent = existing.TrimEnd() + (existing.Trim().Length > 0 ? "\n" : "") + line + "\n";
        File.WriteAllText(reqTxt, newContent);
        return ($"Added '{line}' to requirements.txt (run 'pip install -r requirements.txt' to install).", true);
    }

    private static async Task<(string, bool)> RunTestsAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var testCommand = GetArg(args, "test_command");
        if (!string.IsNullOrEmpty(testCommand))
            return await RunCommandAsync(new() { { "command", testCommand } }, ct);

        var pkgJson = Path.Combine(Ws, "package.json");
        if (File.Exists(pkgJson))
        {
            try
            {
                var data = JObject.Parse(File.ReadAllText(pkgJson));
                if (data["scripts"]?["test"] != null)
                    return await RunCommandAsync(new() { { "command", "npm test" } }, ct);
            }
            catch { }
        }

        // Check for Python test files
        var hasPyTests = Directory.GetFiles(Ws, "test_*.py", SearchOption.AllDirectories).Length > 0
            || Directory.GetFiles(Ws, "*_test.py", SearchOption.AllDirectories).Length > 0;

        if (hasPyTests)
        {
            // Try pytest first
            var (pyCheck, _) = await RunCommandAsync(new() { { "command", "python -m pytest --version" } }, ct);
            if (pyCheck.Contains("pytest"))
                return await RunCommandAsync(new() { { "command", "python -m pytest -v" } }, ct);

            var (result, success) = await RunCommandAsync(new() { { "command", "python -m unittest discover -v" } }, ct);
            return (result + "\nNote: pytest not installed, so only unittest.TestCase-style tests are discovered.", success);
        }

        return ("Could not auto-detect a test command (no package.json test script, no test_*.py/*_test.py files found). Pass test_command explicitly.", false);
    }

    private static (string, bool) CreateTestFile(Dictionary<string, object> args)
    {
        var sourcePath = GetArg(args, "source_path") ?? "";
        var testPath = GetArg(args, "test_path");

        var src = Resolve(sourcePath);
        if (!File.Exists(src)) return ($"Source file not found: {sourcePath}", false);

        var ext = Path.GetExtension(src).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(src);
        var relParent = Path.GetDirectoryName(sourcePath) ?? ".";
        var parentPrefix = relParent == "." ? "" : relParent.Replace("\\", "/") + "/";

        string defaultTestPath;
        string content;

        if (ext == ".py")
        {
            defaultTestPath = $"{parentPrefix}test_{stem}.py";
            var q = "\"\"\"";  // Python docstring quotes
            content = $"{q}Tests for {sourcePath}.{q}\nimport pytest\n\ndef test_{stem}_placeholder():\n    // TODO: import from '{stem}' and write real assertions\n    assert True\n";
        else if (ext is ".js" or ".jsx" or ".ts" or ".tsx")
        {
            defaultTestPath = $"{parentPrefix}{stem}.test{ext}";
            content = $"// Tests for {sourcePath}\n"
                + $"describe('{stem}', () => {{\n"
                + $"  test('placeholder', () => {{\n"
                + $"    // TODO: import and write real assertions\n"
                + $"    expect(true).toBe(true);\n"
                + $"  }});\n"
                + $"}});\n";
        }
        else
        {
            return ($"Don't know how to scaffold tests for '{ext}' files (supported: .py, .js, .jsx, .ts, .tsx).", false);
        }

        var finalPath = testPath ?? defaultTestPath;
        return CreateFile(new() { { "path", finalPath }, { "content", content } });
    }

    private static (string, bool) GenerateReadme(Dictionary<string, object> args)
    {
        var projectName = GetArg(args, "project_name") ?? Path.GetFileName(Ws) ?? "Project";

        var hasPkgJson = File.Exists(Path.Combine(Ws, "package.json"));
        var hasReqTxt = File.Exists(Path.Combine(Ws, "requirements.txt"));
        var hasPyproject = File.Exists(Path.Combine(Ws, "pyproject.toml"));

        var setupLines = new List<string>();
        if (hasPkgJson) setupLines.Add("```bash\nnpm install\n```" );
        if (hasReqTxt) setupLines.Add("```bash\npip install -r requirements.txt\n```" );
        if (hasPyproject) setupLines.Add("```bash\npip install .\n```" );
        if (setupLines.Count == 0) setupLines.Add("_(Add setup instructions here.)_");

        var runLines = new List<string>();
        if (hasPkgJson)
        {
            try
            {
                var data = JObject.Parse(File.ReadAllText(Path.Combine(Ws, "package.json")));
                if (data["scripts"]?["start"] != null) runLines.Add("```bash\nnpm start\n```" );
                else if (data["scripts"]?["dev"] != null) runLines.Add("```bash\nnpm run dev\n```" );
            }
            catch { }
        }
        if (runLines.Count == 0) runLines.Add("_(Add run instructions here.)_");

        // Build tree
        var treeLines = new List<string>();
        try
        {
            foreach (var entry in Directory.GetFileSystemEntries(Ws).OrderBy(e => e))
            {
                var name = Path.GetFileName(entry);
                if (name is "node_modules" or ".git" or "__pycache__" or ".venv" or "venv" or ".undo_history")
                    continue;
                var icon = Directory.Exists(entry) ? "[DIR]" : "[FILE]";
                treeLines.Add($"- {icon} `{name}`");
            }
        }
        catch { }
        var tree = treeLines.Count > 0 ? string.Join("\n", treeLines) : "_(empty)_";

        var content = $"# {projectName}\n\n"
            + "## Description\n\n_(Add a short description of what this project does.)_\n\n"
            + "## Setup\n\n" + string.Join("\n", setupLines) + "\n\n"
            + "## Usage\n\n" + string.Join("\n", runLines) + "\n\n"
            + "## Project Structure\n\n" + tree + "\n";

        return CreateFile(new() { { "path", "README.md" }, { "content", content } });
    }

    // ===== PYTHON ANALYSIS =====
    private static (string, bool) ExtractDocstrings(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path") ?? "";
        var fp = Resolve(path);
        if (!File.Exists(fp)) return ($"File not found: {path}", false);
        if (!fp.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            return ($"extract_docstrings only supports Python (.py) files.", false);

        var source = File.ReadAllText(fp);
        var results = new List<string>();

        // Extract module docstring (first string literal if it's a standalone expression)
        var moduleDocMatch = Regex.Match(source, @"^\s*""""([\s\S]*?)""""|^\s*'''([\s\S]*?)'''");
        if (moduleDocMatch.Success)
            results.Add($"Module docstring:\n  {(moduleDocMatch.Groups[1].Success ? moduleDocMatch.Groups[1].Value : moduleDocMatch.Groups[2].Value).Trim()}");

        // Extract function/class docstrings
        var funcPattern = @"(?:^|\n)\s*(?:async\s+)?def\s+(\w+)\s*\([^)]*\)";
        var classPattern = @"(?:^|\n)\s*class\s+(\w+)";
        var allLines = source.Split('\n');

        foreach (Match m in Regex.Matches(source, funcPattern))
        {
            var name = m.Groups[1].Value;
            var lineNum = source[..m.Index].Count(c => c == '\n') + 1;
            var doc = ExtractDocstringAfterMatch(source, m.Index + m.Length);
            results.Add($"function {name}() (line {lineNum}):\n  {(doc ?? "(no docstring)")}");
        }

        foreach (Match m in Regex.Matches(source, classPattern))
        {
            var name = m.Groups[1].Value;
            var lineNum = source[..m.Index].Count(c => c == '\n') + 1;
            var doc = ExtractDocstringAfterMatch(source, m.Index + m.Length);
            results.Add($"class {name}() (line {lineNum}):\n  {(doc ?? "(no docstring)")}");
        }

        if (results.Count == 0) return ($"No functions, classes, or module docstring found in {path}.", true);
        return (string.Join("\n\n", results), true);
    }

    private static string? ExtractDocstringAfterMatch(string source, int startIndex)
    {
        if (startIndex >= source.Length) return null;
        // Find first triple-quote string after the match
        var tripleQuote = Regex.Match(source.Substring(startIndex), @"\s*""""([\s\S]*?)""""|\s*'''([\s\S]*?)'''");
        if (tripleQuote.Success)
        {
            var text = tripleQuote.Groups[1].Success ? tripleQuote.Groups[1].Value : tripleQuote.Groups[2].Value;
            // Make sure it's right after the def/class (not some other string)
            var between = source.Substring(startIndex, tripleQuote.Index);
            if (between.Trim().Length <= 2) // just whitespace and maybe ':'
                return text.Trim();
        }
        return null;
    }

    private static (string, bool) FindUnusedImports(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path") ?? "";
        var fp = Resolve(path);
        if (!File.Exists(fp)) return ($"File not found: {path}", false);
        if (!fp.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            return ($"find_unused_imports only supports Python (.py) files.", false);

        var source = File.ReadAllText(fp);
        var importPattern = @"^\s*(?:from\s+(\S+)\s+import\s+(.+)|(import\s+(.+)))";
        var importedNames = new Dictionary<string, int>(); // name -> line number
        var allLines = source.Split('\n');

        for (int i = 0; i < allLines.Length; i++)
        {
            var m = Regex.Match(allLines[i], importPattern);
            if (!m.Success) continue;

            if (m.Groups[3].Success) // import x, y, z
            {
                foreach (var name in m.Groups[3].Value.Split(',').Select(n => n.Trim().Split('.')[0].Split(" as ")[0].Trim()).Where(n => !string.IsNullOrEmpty(n)))
                    importedNames[name] = i + 1;
            }
            else if (m.Groups[1].Success && m.Groups[2].Success) // from x import y, z
            {
                foreach (var name in m.Groups[2].Value.Split(',').Select(n => n.Trim().Split(" as ")[0].Trim()).Where(n => !string.IsNullOrEmpty(n) && n != "*"))
                    importedNames[name] = i + 1;
            }
        }

        if (importedNames.Count == 0) return ($"No imports found in {path}.", true);

        // Check for __all__ exports
        var allExportNames = new HashSet<string>();
        var allMatch = Regex.Match(source, @"__all__\s*=\s*\[([^\]]*)\]");
        if (allMatch.Success)
        {
            foreach (var name in Regex.Matches(allMatch.Groups[1].Value, @"[""']([^""']+)['""]").Cast<Match>().Select(m => m.Groups[1].Value))
                allExportNames.Add(name);
        }

        // Simple name usage counting: for each imported name, check if it appears elsewhere in the file
        var unused = new List<string>();
        foreach (var kv in importedNames.OrderBy(x => x.Value))
        {
            if (allExportNames.Contains(kv.Key)) continue;

            // Count occurrences of the name as a whole word (not in import lines)
            var namePattern = $@"\b{Regex.Escape(kv.Key)}\b";
            var nameMatches = Regex.Matches(source, namePattern);
            int importLineUsages = 0;
            int otherUsages = 0;
            foreach (Match nm in nameMatches)
            {
                var lineIdx = source[..nm.Index].Count(c => c == '\n');
                if (Regex.IsMatch(allLines[lineIdx], @"^\s*(import|from)\s+"))
                    importLineUsages++;
                else
                    otherUsages++;
            }
            if (otherUsages == 0)
                unused.Add($"  line {kv.Value}: '{kv.Key}' appears unused");
        }

        if (unused.Count == 0) return ($"No unused imports detected in {path}.", true);
        return ($"Possibly unused imports in {path} (double-check before removing -- dynamic usage can cause false positives):\n" + string.Join("\n", unused), true);
    }

    // ===== CHECKPOINTS =====
    private static (string, bool) SaveCheckpoint(Dictionary<string, object> args)
    {
        var name = GetArg(args, "name") ?? "";
        var description = GetArg(args, "description") ?? "";
        if (string.IsNullOrEmpty(name)) return ("No checkpoint name provided.", false);

        var checkpointsDir = Path.Combine(Ws, ".undo_history", "checkpoints");
        Directory.CreateDirectory(checkpointsDir);
        var safeName = Regex.Replace(name, @"[^a-zA-Z0-9_-]", "_");
        var checkpointZip = Path.Combine(checkpointsDir, $"{safeName}.zip");

        if (File.Exists(checkpointZip))
            return ($"A checkpoint named '{name}' already exists. Choose a different name.", false);

        var ignoreDirs = new HashSet<string> { "node_modules", ".git", "__pycache__", ".venv", "venv", ".undo_history" };

        try
        {
            using var zip = ZipFile.Open(checkpointZip, ZipArchiveMode.Create);
            foreach (var file in Directory.GetFiles(Ws, "*.*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(Ws, file);
                var parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                if (parts.Any(p => ignoreDirs.Contains(p))) continue;
                zip.CreateEntryFromFile(file, rel);
            }
        }
        catch (Exception ex) { return ($"Failed to create checkpoint: {ex.Message}", false); }

        var meta = new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        File.WriteAllText(Path.Combine(checkpointsDir, $"{safeName}.json"), meta.ToString(Formatting.Indented));

        return ($"Checkpoint '{name}' saved.", true);
    }

    private static (string, bool) LoadCheckpoint(Dictionary<string, object> args)
    {
        var name = GetArg(args, "name") ?? "";
        if (string.IsNullOrEmpty(name)) return ("No checkpoint name provided.", false);

        var checkpointsDir = Path.Combine(Ws, ".undo_history", "checkpoints");
        var safeName = Regex.Replace(name, @"[^a-zA-Z0-9_-]", "_");
        var checkpointZip = Path.Combine(checkpointsDir, $"{safeName}.zip");

        if (!File.Exists(checkpointZip))
            return ($"No checkpoint named '{name}' found. Use list_checkpoints to see available ones.", false);

        try
        {
            ZipFile.ExtractToDirectory(checkpointZip, Ws, overwriteFiles: true);
            return ($"Workspace restored to checkpoint '{name}'.", true);
        }
        catch (Exception ex) { return ($"Failed to restore checkpoint: {ex.Message}", false); }
    }

    private static (string, bool) ListCheckpoints()
    {
        var checkpointsDir = Path.Combine(Ws, ".undo_history", "checkpoints");
        if (!Directory.Exists(checkpointsDir)) return ("No checkpoints saved yet.", true);

        var lines = new List<string>();
        foreach (var metaFile in Directory.GetFiles(checkpointsDir, "*.json").OrderBy(f => f))
        {
            try
            {
                var meta = JObject.Parse(File.ReadAllText(metaFile));
                var ts = meta["ts"]?.ToObject<long>() ?? 0;
                var when = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                var desc = meta["description"]?.ToString();
                var descSuffix = !string.IsNullOrEmpty(desc) ? $" -- {desc}" : "";
                lines.Add($"{meta["name"]} ({when}){descSuffix}");
            }
            catch { }
        }

        if (lines.Count == 0) return ("No checkpoints saved yet.", true);
        return (string.Join("\n", lines), true);
    }

    // ===== METRICS =====
    private static (string, bool) CountLinesOfCode()
    {
        var commentPrefixes = new Dictionary<string, string>
        {
            [".py"] = "#", [".sh"] = "#", [".rb"] = "#", [".yaml"] = "#", [".yml"] = "#",
            [".js"] = "//", [".jsx"] = "//", [".ts"] = "//", [".tsx"] = "//", [".java"] = "//",
            [".c"] = "//", [".cpp"] = "//", [".h"] = "//", [".hpp"] = "//", [".cs"] = "//",
            [".go"] = "//", [".rs"] = "//", [".swift"] = "//", [".kt"] = "//"
        };
        var ignoreDirs = new HashSet<string> { "node_modules", ".git", "__pycache__", ".venv", "venv", ".undo_history" };

        var statsByExt = new Dictionary<string, Dictionary<string, int>>();

        foreach (var file in Directory.GetFiles(Ws, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Ws, file);
            var parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (parts.Any(p => ignoreDirs.Contains(p))) continue;

            var ext = Path.GetExtension(file).ToLowerInvariant() ?? "(no extension)";
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }

            if (!statsByExt.ContainsKey(ext))
                statsByExt[ext] = new() { ["files"] = 0, ["total"] = 0, ["blank"] = 0, ["comment"] = 0, ["code"] = 0 };
            var entry = statsByExt[ext];
            entry["files"]++;
            var prefix = commentPrefixes.GetValueOrDefault(ext);

            foreach (var line in lines)
            {
                var stripped = line.Trim();
                entry["total"]++;
                if (string.IsNullOrEmpty(stripped)) entry["blank"]++;
                else if (prefix != null && stripped.StartsWith(prefix)) entry["comment"]++;
                else entry["code"]++;
            }
        }

        if (statsByExt.Count == 0) return ("No files found in the workspace.", true);

        var linesOut = new List<string> { "Lines of code by extension:\n" };
        int totalFiles = 0, totalLines = 0, totalBlank = 0, totalComment = 0, totalCode = 0;

        foreach (var kv in statsByExt.OrderByDescending(x => x.Value["total"]))
        {
            var s = kv.Value;
            totalFiles += s["files"]; totalLines += s["total"]; totalBlank += s["blank"]; totalComment += s["comment"]; totalCode += s["code"];
            linesOut.Add($"  {kv.Key}: {s["files"]} file(s), {s["total"]} lines ({s["code"]} code, {s["comment"]} comment, {s["blank"]} blank)");
        }

        linesOut.Add($"\nTotal: {totalFiles} file(s), {totalLines} lines ({totalCode} code, {totalComment} comment, {totalBlank} blank)");
        return (string.Join("\n", linesOut), true);
    }

    // ===== FILE CONVERSION =====
    private static (string, bool) ConvertFileFormat(Dictionary<string, object> args)
    {
        var sourcePath = GetArg(args, "source_path") ?? "";
        var destinationPath = GetArg(args, "destination_path") ?? "";
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(destinationPath))
            return ("Both source_path and destination_path are required.", false);

        var src = Resolve(sourcePath);
        if (!File.Exists(src)) return ($"Source file not found: {sourcePath}", false);

        var srcExt = Path.GetExtension(src).ToLowerInvariant();
        var dstExt = Path.GetExtension(destinationPath).ToLowerInvariant();

        try
        {
            string output;

            if (srcExt == ".json" && (dstExt == ".yaml" || dstExt == ".yml"))
            {
                // JSON -> YAML (simple conversion without PyYAML: just format nested structure)
                var data = JToken.Parse(File.ReadAllText(src));
                output = ConvertJsonToYaml(data, 0);
            }
            else if ((srcExt == ".yaml" || srcExt == ".yml") && dstExt == ".json")
            {
                // Simple YAML parser for basic structures (key: value, lists with -)
                var yaml = File.ReadAllText(src);
                var data = ParseSimpleYaml(yaml);
                output = data.ToString(Formatting.Indented);
            }
            else if (srcExt == ".json" && dstExt == ".csv")
            {
                var data = JArray.Parse(File.ReadAllText(src));
                if (data.Count == 0 || !(data[0] is JObject))
                    return (".json -> .csv conversion requires a JSON array of flat objects.", false);
                var headers = ((JObject)data[0]).Properties().Select(p => p.Name).ToList();
                var csvLines = new List<string> { string.Join(",", headers) };
                foreach (var item in data)
                {
                    var obj = (JObject)item;
                    var row = headers.Select(h =>
                    {
                        var val = obj[h]?.ToString() ?? "";
                        if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
                            val = $"\"{val.Replace("\"", "\"\"")}\"";
                        return val;
                    });
                    csvLines.Add(string.Join(",", row));
                }
                output = string.Join("\n", csvLines);
            }
            else if (srcExt == ".csv" && dstExt == ".json")
            {
                var lines = File.ReadAllLines(src);
                if (lines.Length == 0) return ("CSV file is empty.", false);
                var headers = lines[0].Split(',').Select(h => h.Trim().Trim('"')).ToList();
                var arr = new JArray();
                foreach (var line in lines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var values = ParseCsvLine(line);
                    var obj = new JObject();
                    for (int i = 0; i < headers.Count; i++)
                        obj[headers[i]] = i < values.Count ? values[i] : "";
                    arr.Add(obj);
                }
                output = arr.ToString(Formatting.Indented);
            }
            else
            {
                return ($"Unsupported conversion: {srcExt} -> {dstExt}. Supported: json<->yaml, json<->csv.", false);
            }

            return CreateFile(new() { { "path", destinationPath }, { "content", output } });
        }
        catch (Exception ex) { return ($"Conversion failed: {ex.Message}", false); }
    }

    private static string ConvertJsonToYaml(JToken token, int indent)
    {
        var pad = new string(' ', indent * 2);
        if (token is JObject obj)
        {
            var lines = new List<string>();
            foreach (var prop in obj.Properties())
            {
                if (prop.Value is JArray arr && arr.Count > 0)
                {
                    lines.Add($"{pad}{prop.Name}:");
                    foreach (var item in arr)
                        lines.Add($"{pad}  - {FormatYamlValue(item, indent + 2)}");
                }
                else if (prop.Value is JValue val && (val.Type == JTokenType.String || val.Type == JTokenType.Integer || val.Type == JTokenType.Float || val.Type == JTokenType.Boolean))
                {
                    lines.Add($"{pad}{prop.Name}: {FormatYamlValue(prop.Value, 0)}");
                }
                else if (prop.Value is JObject)
                {
                    lines.Add($"{pad}{prop.Name}:");
                    lines.Add(ConvertJsonToYaml(prop.Value, indent + 1));
                }
                else if (prop.Value is JArray)
                {
                    lines.Add($"{pad}{prop.Name}: []");
                }
                else
                {
                    lines.Add($"{pad}{prop.Name}: {FormatYamlValue(prop.Value, 0)}");
                }
            }
            return string.Join("\n", lines);
        }
        return token.ToString();
    }

    private static string FormatYamlValue(JToken token, int indent)
    {
        if (token is JValue val)
        {
            if (val.Type == JTokenType.String)
            {
                var s = val.ToString();
                if (s.Contains(':') || s.Contains('#') || s.Contains('\n') || string.IsNullOrEmpty(s))
                    return $"\"{s.Replace("\"", "\\\"")}\"";
                return s;
            }
            return val.ToString();
        }
        if (token is JArray) return "[]";
        if (token is JObject) return ConvertJsonToYaml(token, indent);
        return token.ToString();
    }

    private static JToken ParseSimpleYaml(string yaml)
    {
        // Very basic YAML parser for simple key-value and list structures
        var lines = yaml.Split('\n');
        var result = new JObject();
        JToken? currentListKey = null;
        JArray? currentArray = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

            var listMatch = Regex.Match(trimmed, @"^- (.+)$");
            if (listMatch.Success && currentListKey != null)
            {
                currentArray?.Add(listMatch.Groups[1].Value.Trim());
                continue;
            }

            var kvMatch = Regex.Match(trimmed, @"^(\w[\w\s.]*?):\s*(.+)$");
            if (kvMatch.Success)
            {
                currentListKey = null;
                currentArray = null;
                var key = kvMatch.Groups[1].Value.Trim();
                var value = kvMatch.Groups[2].Value.Trim();

                // Try to parse value as number or boolean
                if (int.TryParse(value, out var intVal)) result[key] = intVal;
                else if (double.TryParse(value, out var dblVal)) result[key] = dblVal;
                else if (value.ToLowerInvariant() is "true" or "false") result[key] = value.ToLowerInvariant() == "true";
                else result[key] = value;

                // Check if next lines are a list
                currentListKey = key;
                currentArray = new JArray();
                result[key] = currentArray;
                continue;
            }

            // Reset list context on non-list, non-kv lines
            if (!listMatch.Success)
            {
                currentListKey = null;
                currentArray = null;
            }
        }
        return result;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                { current.Append('"'); i++; }
                else if (c == '"') { inQuotes = false; }
                else { current.Append(c); }
            }
            else
            {
                if (c == '"') { inQuotes = true; }
                else if (c == ',') { result.Add(current.ToString()); current.Clear(); }
                else { current.Append(c); }
            }
        }
        result.Add(current.ToString());
        return result;
    }

    private static (string, bool) MinifyFile(Dictionary<string, object> args)
    {
        var path = GetArg(args, "path") ?? "";
        var outputPath = GetArg(args, "output_path");
        var fp = Resolve(path);
        if (!File.Exists(fp)) return ($"File not found: {path}", false);

        var ext = Path.GetExtension(fp).ToLowerInvariant();
        var content = File.ReadAllText(fp);
        string minified;

        if (ext == ".json")
        {
            try
            {
                var data = JToken.Parse(content);
                var sb = new StringBuilder();
                using var writer = new StringWriter(sb);
                using var jsonWriter = new JsonTextWriter(writer);
                data.WriteTo(jsonWriter);
                // Re-serialize without formatting
                minified = JsonConvert.SerializeObject(JObject.Parse(content), Formatting.None);
            }
            catch (JsonException ex) { return ($"Invalid JSON, cannot minify: {ex.Message}", false); }
        }
        else if (ext == ".css")
        {
            minified = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
            minified = Regex.Replace(minified, @"\s+", " ");
            minified = Regex.Replace(minified, @"\s*([{}:;,])\s*", "$1");
            minified = minified.Trim();
        }
        else if (ext is ".js" or ".jsx")
        {
            minified = Regex.Replace(content, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var lines = minified.Split('\n').Where(l => l.Trim().Length > 0 && !l.Trim().StartsWith("//"));
            minified = string.Join("\n", lines);
        }
        else
        {
            return ($"minify_file only supports .json, .css, .js/.jsx files, got: {ext}", false);
        }

        var target = outputPath ?? path;
        return CreateFile(new() { { "path", target }, { "content", minified } });
    }

    // ===== HELPERS =====
    private static string Resolve(string rel)
    {
        var combined = Path.GetFullPath(Path.Combine(Ws, rel));
        if (!combined.StartsWith(Ws)) combined = Path.Combine(Ws, Path.GetFileName(rel));
        return combined;
    }

    private static string? GetArg(Dictionary<string, object> args, string key) => args.TryGetValue(key, out var v) ? v?.ToString() : null;
    private static bool GetBoolArg(Dictionary<string, object> args, string key) => args.TryGetValue(key, out var v) && (v?.ToString()?.ToLowerInvariant() is "true" or "1" or "yes");

    private static void SaveUndo(string filePath)
    {
        if (!File.Exists(filePath)) return;
        _undoStack.Add(new UndoEntry { FilePath = filePath, OldContent = File.ReadAllText(filePath), Time = DateTime.Now });
        if (_undoStack.Count > MAX_UNDO) _undoStack.RemoveAt(0);
    }

    private static (string, bool) RunGit(string? path, string args)
    {
        var dir = string.IsNullOrEmpty(path) ? Ws : Resolve(path);
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo { FileName = "git", Arguments = args, WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            p.Start();
            var out1 = p.StandardOutput.ReadToEnd();
            var err1 = p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (string.IsNullOrEmpty(out1) ? (string.IsNullOrEmpty(err1) ? "OK" : err1) : out1, true);
        }
        catch (Exception ex) { return ($"Git error: {ex.Message}", false); }
    }

    /// <summary>
    /// Returns Gemini function-calling tool declarations for ALL tools.
    /// </summary>
    public static List<Dictionary<string, object>> GetToolDefinitions() => new()
    {
        Tool("create_file", "Creates a new file with the given content.", 
            Req("path", "string", "relative file path"), Req("content", "string", "file content")),
        Tool("read_file", "Reads file content.",
            Req("path", "string", "relative file path")),
        Tool("edit_file", "Replaces old_text with new_text in a file.",
            Req("path", "string", "file path"), Req("old_text", "string", "text to find"), Req("new_text", "string", "replacement")),
        Tool("delete_file", "Deletes a file or folder.",
            Req("path", "string", "path to delete")),
        Tool("list_files", "Lists files and folders in a directory.",
            Opt("path", "string", "directory path (default .)")),
        Tool("find_file", "Finds files matching a glob pattern.",
            Req("pattern", "string", "glob pattern"), Opt("path", "string", "search directory")),
        Tool("find_folder", "Finds folders matching a pattern.",
            Req("pattern", "string", "folder pattern"), Opt("path", "string", "search directory")),
        Tool("search_in_files", "Searches for text in files.",
            Req("query", "string", "text to find"), Opt("path", "string", "directory"), Opt("extension", "string", "file extension filter")),
        Tool("file_stats", "Shows file statistics.",
            Req("path", "string", "file path")),
        Tool("detect_language", "Detects programming language from file extension.",
            Req("path", "string", "file path")),
        Tool("count_files", "Counts files.",
            Opt("path", "string", "directory"), Opt("extension", "string", "extension filter")),
        Tool("count_todos", "Counts TODO/FIXME/HACK comments.",
            Opt("path", "string", "directory"), Opt("extension", "string", "extension")),
        Tool("replace_in_files", "Find/replace across multiple files.",
            Req("old_text", "string", "text to find"), Req("new_text", "string", "replacement"),
            Opt("path", "string", "directory"), Opt("extension", "string", "extension filter"),
            Opt("dry_run", "boolean", "preview only"), Opt("whole_word", "boolean", "match whole words")),
        Tool("lint_check", "Basic lint check on a file.",
            Req("path", "string", "file path")),
        Tool("check_file_syntax_all", "Runs lint_check on all supported files in the workspace and returns a summary."),
        Tool("diff_preview", "Shows a diff preview.",
            Req("path", "string", "file name"), Req("old_text", "string", "original"), Req("new_text", "string", "modified")),
        Tool("compare_files", "Compares two files.",
            Req("file1", "string", "first file"), Req("file2", "string", "second file")),
        Tool("move_file", "Moves a file.",
            Req("source", "string", "source"), Req("destination", "string", "dest")),
        Tool("copy_file", "Copies a file.",
            Req("source", "string", "source"), Req("destination", "string", "dest")),
        Tool("rename_file", "Renames a file.",
            Req("path", "string", "file path"), Req("new_name", "string", "new name")),
        Tool("create_folder", "Creates a folder.",
            Req("path", "string", "folder path")),
        Tool("git_clone", "Clones a git repo.",
            Req("url", "string", "repo URL"), Opt("path", "string", "destination")),
        Tool("git_fetcher", "Fetches GitHub repo metadata WITHOUT cloning (via GitHub API).",
            Req("repo", "string", "owner/name or full GitHub URL"), Opt("include_tree", "boolean", "also fetch top-level file tree")),
        Tool("git_status", "Shows git status.", Opt("path", "string", "repo path")),
        Tool("git_diff", "Shows git diff.", Opt("path", "string", "repo path")),
        Tool("git_log", "Shows recent commits.", Opt("path", "string", "repo path")),
        Tool("git_commit", "Stages all and commits.",
            Req("message", "string", "commit message"), Opt("path", "string", "repo path")),
        Tool("run_command", "Runs a shell command in the workspace.",
            Req("command", "string", "command to run")),
        Tool("start_background_process", "Starts a long-running command in the background, returns PID.",
            Req("command", "string", "command to run"), Opt("working_dir", "string", "working directory (default .)")),
        Tool("list_background_processes", "Lists all background processes with their status."),
        Tool("read_background_log", "Reads the output log of a background process.",
            Req("pid", "string", "process ID"), Opt("tail_lines", "string", "number of lines to show (default 50)")),
        Tool("stop_background_process", "Stops a background process by PID.",
            Req("pid", "string", "process ID")),
        Tool("wait_process", "Waits for a background process to finish or timeout.",
            Req("pid", "string", "process ID"), Opt("timeout", "string", "max seconds to wait (default 60)"), Opt("poll_interval", "string", "seconds between checks (default 1.0)")),
        Tool("create_zip", "Creates a zip archive.",
            Req("source", "string", "source directory"), Opt("output_path", "string", "output filename")),
        Tool("extract_zip", "Extracts a zip archive.",
            Req("archive_path", "string", "archive path"), Opt("destination", "string", "extract destination")),
        Tool("zip_workspace", "Zips the workspace.",
            Opt("output_path", "string", "output filename")),
        Tool("undo_last_change", "Reverts the most recent file change."),
        Tool("Available_Active_Windows", "Lists active windows. Requires system access enabled."),
        Tool("List_System_Processes", "Lists system processes. Requires system access enabled."),
        Tool("check_port_in_use", "Checks if a port is in use.",
            Opt("port", "string", "port number")),
        Tool("http_request", "Sends an HTTP request and returns the response.",
            Req("url", "string", "URL to request"), Opt("method", "string", "HTTP method (default GET)"),
            Opt("headers", "object", "request headers as JSON object"), Opt("body", "string", "request body"), Opt("timeout", "string", "timeout in seconds (default 15)")),
        Tool("env_var_check", "Checks an environment variable.",
            Req("name", "string", "variable name")),
        Tool("Image_Fetch", "Analyzes image files using Gemini vision API.",
            Req("path", "string", "image path(s), comma-separated for multiple"), Opt("question", "string", "what to ask about the image")),
        Tool("Image_Fetch_Puter", "Analyzes image via Puter.js vision model (BETA).",
            Req("path", "string", "image path"), Opt("question", "string", "what to ask")),
        Tool("Image_Create", "Generates an image from a text prompt using Gemini.",
            Req("prompt", "string", "image description"), Req("output_path", "string", "output .png path"), Opt("aspect_ratio", "string", "one of: 1:1, 16:9, 9:16, 4:3, 3:2")),
        Tool("Image_Create_Puter", "Generates an image via Puter.js (BETA).",
            Req("prompt", "string", "image description"), Req("output_path", "string", "output .png path")),
        Tool("view_screen", "Takes a screenshot and describes it using Gemini.",
            Opt("question", "string", "what to ask about the screen"), Opt("only_if_changed", "boolean", "skip if screen unchanged"), Opt("change_threshold", "string", "pixel change % threshold (default 2.0)")),
        Tool("view_screen_puter", "Screenshot via Puter.js vision model (BETA).",
            Opt("question", "string", "what to ask about the screen")),
        Tool("watch_screen", "Watches screen for changes over time.",
            Opt("question", "string", "what to look for"), Opt("duration_seconds", "string", "how long to watch (default 30)"),
            Opt("interval_seconds", "string", "seconds between checks (default 5)"), Opt("change_threshold", "string", "change % threshold (default 2.0)"),
            Opt("use_puter", "boolean", "use Puter.js instead of Gemini")),
        Tool("list_dependencies", "Reads dependency manifests and returns a summary of declared dependencies."),
        Tool("add_dependency", "Adds a dependency to the project's manifest.",
            Req("package", "string", "package name"), Opt("dev", "boolean", "dev dependency"), Opt("version", "string", "version specifier")),
        Tool("run_tests", "Runs the project's test suite with auto-detection.",
            Opt("test_command", "string", "explicit command to run instead of auto-detection")),
        Tool("create_test_file", "Creates a starter test file for a source file.",
            Req("source_path", "string", "source file path"), Opt("test_path", "string", "test file path (auto-detected if omitted)")),
        Tool("generate_readme", "Generates a README.md for the project.",
            Opt("project_name", "string", "project name (defaults to workspace folder name)")),
        Tool("extract_docstrings", "Extracts docstrings from a Python file.",
            Req("path", "string", "Python file path")),
        Tool("find_unused_imports", "Finds possibly unused Python imports in a file.",
            Req("path", "string", "Python file path")),
        Tool("save_checkpoint", "Saves a workspace snapshot under a named checkpoint.",
            Req("name", "string", "checkpoint name"), Opt("description", "string", "description")),
        Tool("load_checkpoint", "Restores workspace from a saved checkpoint.",
            Req("name", "string", "checkpoint name")),
        Tool("list_checkpoints", "Lists all saved checkpoints."),
        Tool("count_lines_of_code", "Counts lines of code broken down by language/extension."),
        Tool("convert_file_format", "Converts between JSON, YAML, and CSV formats.",
            Req("source_path", "string", "source file path"), Req("destination_path", "string", "destination file path")),
        Tool("minify_file", "Minifies JSON, CSS, or JS files.",
            Req("path", "string", "file to minify"), Opt("output_path", "string", "output path (defaults to overwriting original)")),
    };

    // Tool definition helpers
    private static Dictionary<string, object> Tool(string name, string desc, params Dictionary<string, object>[] props)
    {
        var parameters = new Dictionary<string, object> { { "type", "object" }, { "properties", new Dictionary<string, object>() }, { "required", new List<string>() } };
        var p = (Dictionary<string, object>)parameters["properties"]!;
        var r = (List<string>)parameters["required"]!;
        foreach (var prop in props)
        {
            p[prop["name"]!.ToString()!] = new Dictionary<string, object> { { "type", prop["type"] }, { "description", prop["description"] } };
            if (prop.ContainsKey("required") && (bool)prop["required"]!) r.Add(prop["name"]!.ToString()!);
        }
        return new() { { "name", name }, { "description", desc }, { "parameters", parameters } };
    }
    private static Dictionary<string, object> Req(string name, string type, string desc) => new() { { "name", name }, { "type", type }, { "description", desc }, { "required", true } };
    private static Dictionary<string, object> Opt(string name, string type, string desc) => new() { { "name", name }, { "type", type }, { "description", desc }, { "required", false } };

    /// <summary>
    /// Lightweight tool declaration for PuterService to build OpenAI-style schemas.
    /// Avoids re-parsing the full Gemini-format tool definitions.
    /// </summary>
    public class ToolDeclaration
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public Dictionary<string, object> Properties { get; set; } = new();
        public List<string> Required { get; set; } = new();
    }

    /// <summary>
    /// Returns simplified tool declarations for all registered tools.
    /// Used by PuterService.BuildAllToolsSchema() to create OpenAI-compatible tool schemas.
    /// </summary>
    public static List<ToolDeclaration> GetToolDeclarations()
    {
        var decls = new List<ToolDeclaration>();
        foreach (var toolDef in GetToolDefinitions())
        {
            var name = toolDef["name"]?.ToString() ?? "";
            var desc = toolDef["description"]?.ToString() ?? "";
            var parameters = toolDef["parameters"] as Dictionary<string, object>;
            if (parameters == null) continue;

            var properties = parameters.TryGetValue("properties", out var p)
                ? p as Dictionary<string, object> ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();
            var required = parameters.TryGetValue("required", out var r)
                ? (r as List<object>)?.Select(x => x?.ToString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList()
                : new List<string>();

            decls.Add(new ToolDeclaration
            {
                Name = name,
                Description = desc,
                Properties = properties,
                Required = required ?? new(),
            });
        }
        return decls;
    }
}
