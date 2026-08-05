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

    // Background processes tracking
    private static readonly Dictionary<int, ProcessInfo> _bgProcesses = new();

    // Undo history
    private static readonly List<UndoEntry> _undoStack = new();
    private const int MAX_UNDO = 50;

    private class ProcessInfo { public int Pid; public string Command; public string LogPath; public DateTime Started; }
    private class UndoEntry { public string FilePath; public string OldContent; public DateTime Time; }

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

                // Diff
                "diff_preview" => DiffPreview(args),
                "compare_files" => CompareFiles(args),

                // Git
                "git_clone" => await GitCloneAsync(args, ct),
                "git_status" => GitStatus(args),
                "git_diff" => GitDiff(args),
                "git_log" => GitLog(args),
                "git_commit" => GitCommit(args),

                // Execution
                "run_command" => await RunCommandAsync(args, ct),
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

                // Environment
                "env_var_check" => EnvVarCheck(args),

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
        if (!Directory.Exists(fp)) return ($"Dir not found", false);
        var entries = Directory.GetFileSystemEntries(fp).Take(200).OrderBy(x => x);
        return (string.Join("\n", entries.Select(e => Path.GetRelativePath(Ws, e))), true);
    }

    private static (string, bool) FindFile(Dictionary<string, object> args)
    {
        var pattern = GetArg(args, "pattern") ?? "*";
        var fp = Resolve(GetArg(args, "path") ?? ".");
        if (!Directory.Exists(fp)) return ($"Dir not found", false);
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
        if (!Directory.Exists(fp)) return ($"Dir not found", false);
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
        if (!File.Exists(fp)) return ($"Not found", false);
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
        if (!File.Exists(fp)) return ($"Not found", false);
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

    // ===== EXECUTION =====
    private static async Task<(string, bool)> RunCommandAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var cmd = GetArg(args, "command") ?? "";
        if (string.IsNullOrEmpty(cmd)) return ("No command", false);
        try
        {
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo
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
            foreach (var p in System.Diagnostics.Process.GetProcesses().OrderByDescending(p => p.WorkingSet64).Take(30))
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

    // ===== ENVIRONMENT =====
    private static (string, bool) EnvVarCheck(Dictionary<string, object> args)
    {
        var name = GetArg(args, "name") ?? "";
        if (string.IsNullOrEmpty(name)) return ("Provide a variable name", false);
        var val = Environment.GetEnvironmentVariable(name);
        return (string.IsNullOrEmpty(val) ? $"{name} is not set." : $"{name}={val}", true);
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
            using var p = new System.Diagnostics.Process();
            p.StartInfo = new System.Diagnostics.ProcessStartInfo { FileName = "git", Arguments = args, WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
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
        Tool("git_status", "Shows git status.", Opt("path", "string", "repo path")),
        Tool("git_diff", "Shows git diff.", Opt("path", "string", "repo path")),
        Tool("git_log", "Shows recent commits.", Opt("path", "string", "repo path")),
        Tool("git_commit", "Stages all and commits.",
            Req("message", "string", "commit message"), Opt("path", "string", "repo path")),
        Tool("run_command", "Runs a shell command in the workspace.",
            Req("command", "string", "command to run")),
        Tool("create_zip", "Creates a zip archive.",
            Req("source", "string", "source directory"), Opt("output_path", "string", "output filename")),
        Tool("extract_zip", "Extracts a zip archive.",
            Req("archive_path", "string", "archive path"), Opt("destination", "string", "extract destination")),
        Tool("zip_workspace", "Zips the workspace.",
            Opt("output_path", "string", "output filename")),
        Tool("undo_last_change", "Reverts the most recent file change.", ),
        Tool("check_port_in_use", "Checks if a port is in use.",
            Opt("port", "string", "port number")),
        Tool("env_var_check", "Checks an environment variable.",
            Req("name", "string", "variable name")),
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
}
