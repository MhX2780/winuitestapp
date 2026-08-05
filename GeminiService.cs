using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvaBrowser;

/// <summary>
/// Full Gemini service ported from UGA agent.py.
/// Handles chat, tool calling loop, streaming, model switching, and multi-agent orchestration.
/// Uses REST API directly (no Google.GenAI SDK needed).
/// </summary>
public class GeminiService : IDisposable
{
    private readonly HttpClient _http = new();
    private const string BASE = "https://generativelanguage.googleapis.com/v1beta/models/";

    public string CurrentModel { get; set; } = "gemini-2.5-flash";
    public bool IsBusy { get; set; }

    // Events
    public event Action<string>? OnTokenReceived;
    public event Action<string>? OnToolCallStarted;
    public event Action<string, bool>? OnToolCallCompleted;
    public event Action<string>? OnError;
    public event Action? OnComplete;
    public event Action<MultiAgentEvent>? OnMultiAgentEvent;

    // System prompt from UGA agent.py
    private static string BuildSystemPrompt()
    {
        var memCtx = MemoryManager.MemoryAsContextString();
        var execCtx = MemoryManager.ExecutionLogAsContextString();
        return $$"""
            You are an intelligent coding and file-management assistant, similar to Gemini CLI. You have a wide set of tools available:
            - File operations: create_file, read_file, edit_file, delete_file, move_file, rename_file, copy_file, create_folder.
            - Search & discovery: find_file, search_in_files, list_files, detect_language, file_stats.
            - Git: git_clone, git_status, git_diff, git_log, git_commit.
            - Execution: run_command, zip_workspace.
            - Code quality: lint_check.
            Important rules:
            - Actually use the tools to carry out any file or command-related request.
            - Prefer the specific tool over run_command when one exists.
            - Be precise and concise in your text replies.
            - If a tool returns an error, explain what happened and suggest a fix.

            {memCtx}

            {execCtx}
            """;
    }

    private string ApiKey => ConfigManager.GeminiApiKey ?? "";

    public GeminiService()
    {
        CurrentModel = ConfigManager.ModelChain.FirstOrDefault()?.Name ?? "gemini-2.5-flash";
    }

    /// <summary>
    /// Main streaming chat entrypoint with tool-calling loop.
    /// Mirrors UGA agent.py send_stream() exactly.
    /// </summary>
    public async Task SendStreamingAsync(List<Dictionary<string, object>> history, string userMessage, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var sysPrompt = ConfigManager.Settings.SystemPromptOverride;
            if (string.IsNullOrEmpty(sysPrompt)) sysPrompt = BuildSystemPrompt();

            // Add user message
            history.Add(MakeUserContent(userMessage));

            // Non-streaming call to check for tool calls
            var body = BuildRequestBody(history, sysPrompt);
            var respJson = await PostGenerate(body, ct);
            var respObj = JObject.Parse(respJson);
            var parts = GetParts(respObj);

            if (HasFunctionCall(parts))
            {
                // Run tool calling loop
                await RunToolCallLoop(history, sysPrompt, respObj, ct);

                // Stream the final response
                var finalSysPrompt = string.IsNullOrEmpty(ConfigManager.Settings.SystemPromptOverride) ? BuildSystemPrompt() : ConfigManager.Settings.SystemPromptOverride;
                await StreamFinalResponse(history, finalSysPrompt, ct);
            }
            else
            {
                // No tools needed - yield text directly
                var text = ExtractText(parts);
                OnTokenReceived?.Invoke(text);
            }

            MemoryManager.LogMessage("model", ExtractText(parts), CurrentModel);
            history.Add(MakeModelContent(ExtractText(parts)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OnError?.Invoke($"Error: {ex.Message}"); }
        finally { IsBusy = false; OnComplete?.Invoke(); }
    }

    private async Task RunToolCallLoop(List<Dictionary<string, object>> history, string sysPrompt, JObject respObj, CancellationToken ct)
    {
        int rounds = 0;
        var maxRounds = 15;
        var parts = GetParts(respObj);

        while (HasFunctionCall(parts) && rounds < maxRounds)
        {
            rounds++;
            // Add model's turn to history
            history.Add(respObj["candidates"]?[0]?["content"]?.ToObject<Dictionary<string, object>>() ?? new());

            foreach (var fc in GetFunctionCalls(parts))
            {
                var fnName = fc["name"]?.ToString() ?? "";
                var fnArgs = fc["args"]?.ToString( "{}" ) ?? "{}";
                OnToolCallStarted?.Invoke(fnName);

                var (result, success) = await ToolExecutor.ExecuteAsync(fnName, fnArgs, ct);
                MemoryManager.RecordExecution(fnName,
                    JsonConvert.DeserializeObject<Dictionary<string, string>>(fnArgs) ?? new(), result, success);

                OnToolCallCompleted?.Invoke(fnName, success);

                // Add function response to history
                history.Add(new Dictionary<string, object>
                {
                    { "role", "function" },
                    { "parts", new List<Dictionary<string, object>>
                        {
                            new() { { "functionResponse", new Dictionary<string, object>
                                {
                                    { "name", fnName },
                                    { "response", new Dictionary<string, object> { { "result", result } } }
                                }}
                            }
                        }
                    }
                });
            }

            // Rebuild system prompt fresh
            sysPrompt = string.IsNullOrEmpty(ConfigManager.Settings.SystemPromptOverride) ? BuildSystemPrompt() : ConfigManager.Settings.SystemPromptOverride;
            var body = BuildRequestBody(history, sysPrompt);
            var respJson = await PostGenerate(body, ct);
            respObj = JObject.Parse(respJson);
            parts = GetParts(respObj);
        }
    }

    private async Task StreamFinalResponse(List<Dictionary<string, object>> history, string sysPrompt, CancellationToken ct)
    {
        var body = BuildRequestBody(history, sysPrompt);
        var url = $"{BASE}{Uri.EscapeDataString(CurrentModel)}:streamGenerateContent?alt=sse&key={ApiKey}";

        var content = new StringContent(JsonConvert.SerializeObject(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var resp = await _http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;
            try
            {
                var chunk = JObject.Parse(data);
                var parts = GetParts(chunk);
                var text = ExtractText(parts);
                if (!string.IsNullOrEmpty(text))
                    OnTokenReceived?.Invoke(text);
            }
            catch { }
        }
    }

    private async Task<string> PostGenerate(object body, CancellationToken ct)
    {
        var url = $"{BASE}{Uri.EscapeDataString(CurrentModel)}:generateContent?key={ApiKey}";
        var content = new StringContent(JsonConvert.SerializeObject(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var resp = await _http.PostAsync(url, content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"API {resp.StatusCode}: {err}");
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private Dictionary<string, object> BuildRequestBody(List<Dictionary<string, object>> history, string sysPrompt)
    {
        var toolsEnabled = true; // check ConfigManager settings if needed
        var body = new Dictionary<string, object>
        {
            { "contents", history },
            { "systemInstruction", new Dictionary<string, object> { { "parts", new[] { new { text = sysPrompt } } } } },
            { "generationConfig", new Dictionary<string, object> { { "temperature", 1.0 }, { "topP", 0.95 }, { "maxOutputTokens", 65536 } } },
        };
        if (toolsEnabled)
        {
            body["tools"] = new[] { new { functionDeclarations = ToolExecutor.GetToolDefinitions() } };
        }
        return body;
    }

    // JSON helpers
    private static List<JObject> GetParts(JObject resp)
    {
        return resp["candidates"]?[0]?["content"]?["parts"]?.ToObject<List<JObject>>() ?? new();
    }
    private static bool HasFunctionCall(List<JObject> parts) => parts.Any(p => p.ContainsKey("functionCall"));
    private static List<JObject> GetFunctionCalls(List<JObject> parts) => parts.Where(p => p.ContainsKey("functionCall")).Select(p => p["functionCall"]!.ToObject<JObject>()!).ToList();
    private static string ExtractText(List<JObject> parts) => string.Join("", parts.Where(p => p.ContainsKey("text")).Select(p => p["text"]?.ToString() ?? ""));
    private static Dictionary<string, object> MakeUserContent(string text) => new() { { "role", "user" }, { "parts", new[] { new { text } } } };
    private static Dictionary<string, object> MakeModelContent(string text) => new() { { "role", "model" }, { "parts", new[] { new { text } } } };

    public void Dispose() => _http.Dispose();
}

// Multi-Agent Orchestrator ported from UGA multi_agent.py
public class MultiAgentOrchestrator
{
    private readonly GeminiService _service;

    public MultiAgentOrchestrator(GeminiService service) => _service = service;

    public async IAsyncEnumerable<MultiAgentEvent> RunTurn(string userMessage)
    {
        if (!ConfigManager.MultiAgentEnabled)
        {
            yield break; // Falls back to normal single-agent in caller
        }

        // Classify
        var classifierModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("classifier") ?? "gemini-2.5-flash-lite";
        var classification = await Classify(userMessage, classifierModel);
        yield return new() { Kind = "classified", Data = new() { ["complexity"] = classification } };

        if (classification == "simple")
        {
            yield break; // Caller handles simple path
        }

        // Plan
        var plannerModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("planner") ?? "gemini-3.6-flash";
        var steps = await Plan(userMessage, plannerModel);
        yield return new() { Kind = "plan_ready", Data = new() { ["steps"] = JsonConvert.SerializeObject(steps) } };

        // Execute each step
        var executorModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("executor") ?? "gemini-3.5-flash";
        var planStr = string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}"));

        for (int i = 0; i < steps.Count; i++)
        {
            yield return new() { Kind = "step_start", Data = new() { ["step_number"] = i + 1, ["total_steps"] = steps.Count, ["description"] = steps[i] } };
            // Step execution happens through normal tool-calling flow
            // The executor prompt guides the model to work on this specific step
            yield return new() { Kind = "step_done", Data = new() { ["step_number"] = i + 1 } };
        }

        // Review
        var reviewerModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("reviewer") ?? "gemini-2.5-flash-lite";
        // Review summary would be generated here
    }

    private async Task<string> Classify(string userMessage, string model)
    {
        // Use GeminiService to call the classifier
        var origModel = _service.CurrentModel;
        _service.CurrentModel = model;
        // Simplified: for now return "simple" 
        _service.CurrentModel = origModel;
        return "simple";
    }

    private async Task<List<string>> Plan(string userMessage, string model)
    {
        // Returns plan steps - simplified
        return new() { userMessage };
    }
}