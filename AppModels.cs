using Newtonsoft.Json;

namespace AdvaBrowser;

// Chat message
public class ChatMessage
{
    [JsonProperty("role")]
    public string Role { get; set; } = "user"; // user, model, system, tool
    [JsonProperty("content")]
    public string Content { get; set; } = "";
    [JsonProperty("model")]
    public string? ModelName { get; set; }
    [JsonProperty("ts")]
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsStreaming { get; set; } = false;
    public bool IsToolCall { get; set; } = false;
    public string? ToolName { get; set; }
    public bool ToolSuccess { get; set; } = true;
    public string ToolCallStatus { get; set; } = "done"; // running, done, failed
    [Newtonsoft.Json.JsonIgnore]
    public bool IsToolRunning => ToolCallStatus == "running";
    [Newtonsoft.Json.JsonIgnore]
    public bool IsToolDone => ToolCallStatus == "done";
    [Newtonsoft.Json.JsonIgnore]
    public bool IsToolFailed => ToolCallStatus == "failed";
    // For multi-agent plan display
    public string? PlanStep { get; set; }
    public int? StepNumber { get; set; }
    public int? TotalSteps { get; set; }
}

// Tool call log entry (execution log)
public class ExecutionLogEntry
{
    [JsonProperty("ts")]
    public double Timestamp { get; set; }
    [JsonProperty("tool")]
    public string ToolName { get; set; } = "";
    [JsonProperty("args")]
    public Dictionary<string, string> Args { get; set; } = new();
    [JsonProperty("result")]
    public string Result { get; set; } = "";
    [JsonProperty("success")]
    public bool Success { get; set; } = true;
}

// Long-term memory entry
public class MemoryEntry
{
    [JsonProperty("ts")]
    public double Timestamp { get; set; }
    [JsonProperty("key")]
    public string Key { get; set; } = "";
    [JsonProperty("value")]
    public string Value { get; set; } = "";
    [JsonProperty("category")]
    public string Category { get; set; } = "general";
}

// Session log entry
public class SessionLogEntry
{
    [JsonProperty("ts")]
    public double Timestamp { get; set; }
    [JsonProperty("role")]
    public string Role { get; set; } = "";
    [JsonProperty("content")]
    public string Content { get; set; } = "";
    [JsonProperty("meta")]
    public Dictionary<string, string>? Meta { get; set; }
}

// Full application settings - mirrors ALL settings from UGA config.py
public class AppSettings
{
    // Model Chain
    public List<ModelChainEntry>? ModelChain { get; set; }
    
    // Multi-Agent
    public bool MultiAgentEnabled { get; set; } = false;
    public Dictionary<string, string>? MultiAgentRoles { get; set; }
    
    // Puter.js
    public bool PuterChatEnabled { get; set; } = false;
    public bool PuterFreeOnly { get; set; } = false;
    public bool PuterToolCallingEnabled { get; set; } = false;
    public bool PuterImageToolsEnabled { get; set; } = false;
    public string PuterVisionModel { get; set; } = "infron:deepseek/deepseek-v4-flash:free";
    public string PuterImageGenModel { get; set; } = "infron:deepseek/deepseek-v4-flash:free";
    public string PuterFreeChatModel { get; set; } = "infron:deepseek/deepseek-v4-flash:free";
    
    // Deep Thinking
    public bool DeepThinkingEnabled { get; set; } = false;
    public int DeepThinkingBudget { get; set; } = -1;
    public bool DeepThinkingIncludeThoughts { get; set; } = true;
    public bool PuterDeepThinkingEnabled { get; set; } = false;
    public string PuterDeepThinkingEffort { get; set; } = "high";
    
    // System Access
    public bool SystemAccessEnabled { get; set; } = false;
    
    // Deep Research
    public string DeepResearchModel { get; set; } = "deep-research-pro-preview-12-2025";
    
    // Workspace
    public string WorkspacePath { get; set; } = "";
    
    // System Prompt Override
    public string SystemPromptOverride { get; set; } = "";
    
    // Max history messages
    public int MaxHistoryMessages { get; set; } = 30;
    
    // Theme: "auto", "dark", "light"
    public string ThemeMode { get; set; } = "auto";

    // Provider API keys (Claude, OpenAI, Puter)
    public string ClaudeApiKey { get; set; } = "";
    public string OpenAIApiKey { get; set; } = "";
    public string PuterToken { get; set; } = "";
}

// Model chain entry
public class ModelChainEntry
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";
    [JsonProperty("max_requests_per_session")]
    public int? MaxRequestsPerSession { get; set; }
}

// Multi-agent event (from multi_agent.py)
public class MultiAgentEvent
{
    public string Kind { get; set; } = ""; // classified, plan_ready, step_start, step_action, step_done, text_chunk
    public Dictionary<string, object> Data { get; set; } = new();
}

// Plan step
public class PlanStep
{
    public int Number { get; set; }
    public string Description { get; set; } = "";
    public string Status { get; set; } = "pending"; // pending, running, done, failed
    public string Result { get; set; } = "";
    public List<string> Actions { get; set; } = new();
}

// Usage stats per model
public class ModelUsageStats
{
    public string ModelName { get; set; } = "";
    public int Requests { get; set; }
    public int Successes { get; set; }
    public int Failures { get; set; }
    public int QuotaExhausted { get; set; }
}

// Provider definition
public class ProviderInfo
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string KeyFile { get; set; } = "";
}
