using Newtonsoft.Json;
namespace AdvaBrowser;

/// <summary>
/// Memory system ported from UGA memory.py.
/// - Session log (JSONL): every message from every session
/// - Long-term memory (JSONL): persistent facts/preferences
/// - Execution log (JSONL): tool-call action/result history
/// </summary>
public static class MemoryManager
{
    // Session log
    public static void LogMessage(string role, string content, string? modelName = null, Dictionary<string, string>? meta = null)
    {
        var record = new SessionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Role = role,
            Content = content,
            Meta = meta != null ? new(meta) : null,
        };
        AppendJsonL(ConfigManager.SessionLogFile, record);
    }

    public static List<SessionLogEntry> LoadRecentSession(int limit = 30)
    {
        return ReadJsonL<SessionLogEntry>(ConfigManager.SessionLogFile)
            .TakeLast(limit).ToList();
    }

    // Long-term memory
    public static void Remember(string key, string value, string category = "general")
    {
        var entry = new MemoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Key = key, Value = value, Category = category,
        };
        AppendJsonL(ConfigManager.LongTermMemoryFile, entry);
    }

    public static void Forget(string key)
    {
        var entries = ReadJsonL<MemoryEntry>(ConfigManager.LongTermMemoryFile)
            .Where(e => e.Key != key).ToList();
        RewriteJsonL(ConfigManager.LongTermMemoryFile, entries);
    }

    public static Dictionary<string, MemoryEntry> RecallAll()
    {
        var result = new Dictionary<string, MemoryEntry>();
        foreach (var entry in ReadJsonL<MemoryEntry>(ConfigManager.LongTermMemoryFile))
            result[entry.Key] = entry;
        return result;
    }

    public static string MemoryAsContextString()
    {
        var mem = RecallAll();
        if (mem.Count == 0) return "";
        var lines = new List<string> { "Saved information about the user from previous conversations:" };
        foreach (var (key, data) in mem)
            lines.Add($"- {key}: {data.Value} (category: {data.Category})");
        return string.Join("\n", lines);
    }

    // Execution log
    public static void RecordExecution(string toolName, Dictionary<string, string> args, string result, bool success)
    {
        var condensedResult = result.Length > 150 ? result[..150] + "..." : result;
        var condensedArgs = new Dictionary<string, string>();
        foreach (var (k, v) in args)
            condensedArgs[k] = v.Length > 60 ? v[..57] + "..." : v;

        var entry = new ExecutionLogEntry
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ToolName = toolName, Args = condensedArgs,
            Result = condensedResult.Replace("\n", " "), Success = success,
        };
        AppendJsonL(ConfigManager.ExecutionLogFile, entry);
    }

    public static List<ExecutionLogEntry> RecentExecutionLog(int limit = 15)
    {
        return ReadJsonL<ExecutionLogEntry>(ConfigManager.ExecutionLogFile).TakeLast(limit).ToList();
    }

    public static string ExecutionLogAsContextString(int limit = 15)
    {
        var entries = RecentExecutionLog(limit);
        if (entries.Count == 0) return "";
        var lines = new List<string>
        {
            "Recent actions already taken in this session (do not repeat these unless something actually needs to be redone):"
        };
        foreach (var e in entries)
        {
            var when = DateTimeOffset.FromUnixTimeSeconds((long)e.Timestamp).DateTime.ToString("HH:mm:ss");
            var status = e.Success ? "OK" : "FAIL";
            var argsStr = string.Join(", ", e.Args.Select(kv => $"{kv.Key}={kv.Value}"));
            lines.Add($"- [{when}] {status} {e.ToolName}({argsStr}) -> {e.Result}");
        }
        return string.Join("\n", lines);
    }

    public static void ClearExecutionLog()
    {
        if (File.Exists(ConfigManager.ExecutionLogFile)) File.Delete(ConfigManager.ExecutionLogFile);
    }

    // Helpers
    private static void AppendJsonL<T>(string path, T record)
    {
        try { File.AppendAllText(path, JsonConvert.SerializeObject(record) + "\n"); }
        catch { }
    }

    private static List<T> ReadJsonL<T>(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            return File.ReadAllLines(path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonConvert.DeserializeObject<T>(l))
                .Where(x => x != null)
                .Cast<T>()
                .ToList();
        }
        catch { return new(); }
    }

    private static void RewriteJsonL<T>(string path, List<T> entries)
    {
        try { File.WriteAllLines(path, entries.Select(e => JsonConvert.SerializeObject(e))); }
        catch { }
    }
}