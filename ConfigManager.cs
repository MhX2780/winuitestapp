using Newtonsoft.Json;
namespace AdvaBrowser;

/// <summary>
/// Full configuration manager ported from UGA config.py.
/// Manages API keys (primary + pool), settings, workspace, model chains, multi-agent roles,
/// Puter.js integration, deep thinking, system access, and deep research.
/// </summary>
public static class ConfigManager
{
    // Base paths
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UGA");
    private static readonly string MemoryDir = Path.Combine(BaseDir, "memory_store");
    private static readonly string SettingsFile = Path.Combine(BaseDir, "settings.json");
    private static readonly string ApiKeyFile = Path.Combine(BaseDir, ".gemini_api_key");
    private static readonly string ApiKeysPoolFile = Path.Combine(BaseDir, ".gemini_api_keys.jsonl");
    private static readonly string PuterTokenFile = Path.Combine(BaseDir, ".puter_token");

    // Memory/log paths
    public static readonly string LongTermMemoryFile = Path.Combine(MemoryDir, "long_term_memory.jsonl");
    public static readonly string SessionLogFile = Path.Combine(MemoryDir, "session_log.jsonl");
    public static readonly string StatsFile = Path.Combine(MemoryDir, "usage_stats.json");
    public static readonly string ExecutionLogFile = Path.Combine(MemoryDir, "execution_log.jsonl");
    public static readonly string ChatHistoryDir = Path.Combine(BaseDir, "chat_history");
    public static readonly string ActiveChatFile = Path.Combine(ChatHistoryDir, "_active.json");
    public static readonly int ExecutionLogContextEntries = 15;

    // Settings
    public static AppSettings Settings { get; set; } = new();

    // Computed from settings
    public static bool HasApiKey => !string.IsNullOrWhiteSpace(LoadSavedApiKey());
    public static string? GeminiApiKey => LoadSavedApiKey();
    public static bool PuterChatEnabled => Settings.PuterChatEnabled;
    public static bool PuterToolCallingEnabled => Settings.PuterToolCallingEnabled;
    public static bool MultiAgentEnabled => Settings.MultiAgentEnabled;
    public static bool DeepThinkingEnabled => Settings.DeepThinkingEnabled;
    public static int DeepThinkingBudget => Settings.DeepThinkingBudget;
    public static bool DeepThinkingIncludeThoughts => Settings.DeepThinkingIncludeThoughts;
    public static bool SystemAccessEnabled => Settings.SystemAccessEnabled;
    public static int MaxHistoryMessages => Settings.MaxHistoryMessages;

    // Default model chain (from config.py)
    public static List<ModelChainEntry> DefaultModelChain => new()
    {
        new() { Name = "gemini-3.6-flash", MaxRequestsPerSession = 200 },
        new() { Name = "gemini-3.5-flash", MaxRequestsPerSession = 200 },
        new() { Name = "gemini-3.5-flash-lite", MaxRequestsPerSession = 300 },
        new() { Name = "gemini-3.1-flash-lite", MaxRequestsPerSession = 300 },
        new() { Name = "gemini-2.5-flash", MaxRequestsPerSession = 200 },
        new() { Name = "gemini-2.5-flash-lite", MaxRequestsPerSession = 300 },
        new() { Name = "gemini-flash-latest", MaxRequestsPerSession = 200 },
        new() { Name = "gemini-flash-lite-latest", MaxRequestsPerSession = 300 },
        new() { Name = "gemma-4-31b-it", MaxRequestsPerSession = 300 },
        new() { Name = "gemma-4-26b-a4b-it", MaxRequestsPerSession = 300 },
        new() { Name = "gemini-2.5-pro", MaxRequestsPerSession = 100 },
        new() { Name = "gemini-pro-latest", MaxRequestsPerSession = 100 },
        new() { Name = "gemini-3.1-pro-preview", MaxRequestsPerSession = null },
    };

    // Default multi-agent roles
    public static Dictionary<string, string> DefaultMultiAgentRoles => new()
    {
        { "classifier", "gemini-2.5-flash-lite" },
        { "planner", "gemini-3.6-flash" },
        { "executor", "gemini-3.5-flash" },
        { "reviewer", "gemini-2.5-flash-lite" },
    };

    // Image model chain
    public static List<string> ImageModelChain => new()
    {
        "gemini-2.5-flash-image",
        "gemini-3.1-flash-image",
        "gemini-3.1-flash-lite-image",
        "gemini-3-pro-image",
        "nano-banana-pro-preview",
        "gemini-3.1-flash-image-preview",
        "gemini-3-pro-image-preview",
    };

    // Active model chain
    public static List<ModelChainEntry> ModelChain => Settings.ModelChain ?? DefaultModelChain;
    public static Dictionary<string, string> MultiAgentRoles => Settings.MultiAgentRoles ?? DefaultMultiAgentRoles;

    // Workspace
    public static string WorkspaceDir
    {
        get
        {
            if (!string.IsNullOrEmpty(Settings.WorkspacePath) && Directory.Exists(Settings.WorkspacePath))
                return Settings.WorkspacePath;
            var ws = Path.Combine(BaseDir, "workspace");
            Directory.CreateDirectory(ws);
            return ws;
        }
    }

    // Retry config
    public static int RetriesPerModel = 2;
    public static int RetryBackoffBase = 2;

    // Providers
    public static readonly Dictionary<string, ProviderInfo> Providers = new()
    {
        ["gemini"] = new() { Id = "gemini", Label = "Google Gemini", KeyFile = ".gemini_api_key" },
        ["claude"] = new() { Id = "claude", Label = "Anthropic Claude", KeyFile = ".claude_api_key" },
        ["openai"] = new() { Id = "openai", Label = "OpenAI GPT", KeyFile = ".openai_api_key" },
        ["puter"] = new() { Id = "puter", Label = "Puter.js", KeyFile = ".puter_token" },
    };

    static ConfigManager()
    {
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(MemoryDir);
        Directory.CreateDirectory(WorkspaceDir);
        Directory.CreateDirectory(ChatHistoryDir);
        LoadSettings();
    }

    // API Key management
    public static string LoadSavedApiKey()
    {
        try { return File.Exists(ApiKeyFile) ? File.ReadAllText(ApiKeyFile).Trim() : ""; }
        catch { return ""; }
    }

    public static void SaveApiKey(string key)
    {
        File.WriteAllText(ApiKeyFile, key.Trim());
    }

    public static void DeleteApiKey()
    {
        if (File.Exists(ApiKeyFile)) File.Delete(ApiKeyFile);
    }

    public static List<string> LoadApiKeyPool()
    {
        var keys = new List<string>();
        var primary = LoadSavedApiKey();
        if (!string.IsNullOrEmpty(primary)) keys.Add(primary);
        if (File.Exists(ApiKeysPoolFile))
        {
            foreach (var line in File.ReadAllLines(ApiKeysPoolFile))
            {
                var k = line.Trim();
                if (!string.IsNullOrEmpty(k) && !keys.Contains(k)) keys.Add(k);
            }
        }
        return keys;
    }

    public static void AddApiKeyToPool(string key)
    {
        key = key.Trim();
        if (string.IsNullOrEmpty(key)) return;
        if (string.IsNullOrEmpty(LoadSavedApiKey())) { SaveApiKey(key); return; }
        var pool = LoadApiKeyPool();
        if (pool.Contains(key)) return;
        File.AppendAllText(ApiKeysPoolFile, key + "\n");
    }

    public static bool RemoveApiKeyFromPool(string keySuffix)
    {
        if (!File.Exists(ApiKeysPoolFile)) return false;
        var lines = File.ReadAllLines(ApiKeysPoolFile).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var matching = lines.Where(l => l.TrimEnd().EndsWith(keySuffix)).ToList();
        if (!matching.Any()) return false;
        var remaining = lines.Where(l => !matching.Contains(l));
        File.WriteAllLines(ApiKeysPoolFile, remaining);
        return true;
    }

    public static string MaskApiKey(string key)
    {
        if (key.Length <= 8) return new string('*', key.Length);
        return $"{key[..4]}...{key[^4..]}";
    }

    // Puter token
    public static string LoadPuterToken()
    {
        try { return File.Exists(PuterTokenFile) ? File.ReadAllText(PuterTokenFile).Trim() : ""; }
        catch { return ""; }
    }

    public static void SavePuterToken(string token) => File.WriteAllText(PuterTokenFile, token.Trim());
    public static void DeletePuterToken() { if (File.Exists(PuterTokenFile)) File.Delete(PuterTokenFile); }

    // Provider API keys
    public static string LoadProviderApiKey(string providerId)
    {
        var fileName = Providers.TryGetValue(providerId, out var p) ? p.KeyFile : $".{providerId}_api_key";
        var path = Path.Combine(BaseDir, fileName);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch { return ""; }
    }

    public static void SaveProviderApiKey(string providerId, string key)
    {
        var fileName = Providers.TryGetValue(providerId, out var p) ? p.KeyFile : $".{providerId}_api_key";
        File.WriteAllText(Path.Combine(BaseDir, fileName), key.Trim());
    }

    // Settings persistence
    public static void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                Settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new();
            }
        }
        catch { Settings = new(); }
    }

    public static void SaveSettings()
    {
        try
        {
            var json = JsonConvert.SerializeObject(GetCurrentSettingsSnapshot(), Formatting.Indented);
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    public static Dictionary<string, object?> GetCurrentSettingsSnapshot()
    {
        return new()
        {
            ["model_chain"] = ModelChain,
            ["multi_agent_roles"] = MultiAgentRoles,
            ["multi_agent_enabled"] = MultiAgentEnabled,
            ["puter_chat_enabled"] = PuterChatEnabled,
            ["puter_free_only"] = Settings.PuterFreeOnly,
            ["puter_tool_calling_enabled"] = PuterToolCallingEnabled,
            ["puter_image_tools_enabled"] = Settings.PuterImageToolsEnabled,
            ["puter_vision_model"] = Settings.PuterVisionModel,
            ["puter_image_gen_model"] = Settings.PuterImageGenModel,
            ["puter_free_chat_model"] = Settings.PuterFreeChatModel,
            ["deep_thinking_enabled"] = DeepThinkingEnabled,
            ["deep_thinking_budget"] = DeepThinkingBudget,
            ["deep_thinking_include_thoughts"] = DeepThinkingIncludeThoughts,
            ["puter_deep_thinking_enabled"] = Settings.PuterDeepThinkingEnabled,
            ["puter_deep_thinking_effort"] = Settings.PuterDeepThinkingEffort,
            ["deep_research_model"] = Settings.DeepResearchModel,
            ["system_access_enabled"] = SystemAccessEnabled,
            ["workspace_path"] = Settings.WorkspacePath,
            ["system_prompt_override"] = Settings.SystemPromptOverride,
            ["max_history_messages"] = MaxHistoryMessages,
        };
    }
}
