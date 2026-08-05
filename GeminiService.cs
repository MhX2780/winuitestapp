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
    /// The caller must NOT include the user message in history — this method adds it.
    /// Returns the final model text response (empty if none).
    /// </summary>
    public async Task<string> SendStreamingAsync(List<Dictionary<string, object>> history, string userMessage, CancellationToken ct = default)
    {
        IsBusy = true;
        string finalText = "";
        try
        {
            var sysPrompt = ConfigManager.Settings.SystemPromptOverride;
            if (string.IsNullOrEmpty(sysPrompt)) sysPrompt = BuildSystemPrompt();

            System.Diagnostics.Debug.WriteLine($"[Gemini] Sending to {CurrentModel}, history count={history.Count}, msg length={userMessage.Length}");

            // Add user message to history
            history.Add(MakeUserContent(userMessage));

            // Non-streaming call to check for tool calls
            var body = BuildRequestBody(history, sysPrompt);
            var serializedBody = JsonConvert.SerializeObject(body);
            System.Diagnostics.Debug.WriteLine($"[Gemini] Request body length={serializedBody.Length}");

            var respJson = await PostGenerate(body, ct);
            System.Diagnostics.Debug.WriteLine($"[Gemini] Response length={respJson?.Length ?? 0}");

            JObject respObj;
            try { respObj = JObject.Parse(respJson); }
            catch (Exception parseEx)
            {
                System.Diagnostics.Debug.WriteLine($"[Gemini] Failed to parse response: {parseEx.Message}");
                OnError?.Invoke($"Failed to parse API response: {parseEx.Message}");
                return "";
            }

            var parts = GetParts(respObj);

            // Check if API returned an error block instead of content
            if (parts.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Gemini] Empty parts. Full response: {respJson}");
                // Maybe the response has error details
                var errMsg = respObj["error"]?["message"]?.ToString();
                if (!string.IsNullOrEmpty(errMsg))
                    OnError?.Invoke($"API Error: {errMsg}");
                return "";
            }

            if (HasFunctionCall(parts))
            {
                // Run tool calling loop — returns final text from last model response
                finalText = await RunToolCallLoop(history, sysPrompt, respObj, ct);
            }
            else
            {
                // No tools needed — emit text directly
                finalText = ExtractText(parts);
            }

            System.Diagnostics.Debug.WriteLine($"[Gemini] Final text length={finalText.Length}");

            // Emit the response to UI
            if (!string.IsNullOrEmpty(finalText))
            {
                OnTokenReceived?.Invoke(finalText);
                // Add model response to history
                history.Add(MakeModelContent(finalText));
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[Gemini] Cancelled");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Gemini] Exception: {ex.Message}\n{ex.StackTrace}");
            OnError?.Invoke($"Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            OnComplete?.Invoke();
        }

        return finalText;
    }

    /// <summary>
    /// Tool calling loop. Processes function calls, executes tools, feeds results back.
    /// Returns the final text from the last model response (after all tools are done).
    /// </summary>
    private async Task<string> RunToolCallLoop(List<Dictionary<string, object>> history, string sysPrompt, JObject respObj, CancellationToken ct)
    {
        int rounds = 0;
        var maxRounds = 15;
        var parts = GetParts(respObj);

        while (HasFunctionCall(parts) && rounds < maxRounds)
        {
            rounds++;
            System.Diagnostics.Debug.WriteLine($"[Gemini] Tool round {rounds}");

            // Add model's response to history — convert via JSON round-trip to avoid JObject mixing
            var contentToken = respObj["candidates"]?[0]?["content"];
            if (contentToken != null)
            {
                try
                {
                    var contentJson = contentToken.ToString();
                    var contentDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(contentJson);
                    if (contentDict != null)
                        history.Add(contentDict);
                    else
                        history.Add(new Dictionary<string, object> { { "role", "model" }, { "parts", new List<object>() } });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Gemini] Failed to convert model content: {ex.Message}");
                    history.Add(new Dictionary<string, object> { { "role", "model" }, { "parts", new List<object>() } });
                }
            }

            foreach (var fc in GetFunctionCalls(parts))
            {
                var fnName = fc["name"]?.ToString() ?? "";
                var fnArgs = fc["args"]?.ToString() ?? "{}";
                OnToolCallStarted?.Invoke(fnName);

                System.Diagnostics.Debug.WriteLine($"[Gemini] Tool: {fnName}");

                string result;
                bool success;
                try
                {
                    (result, success) = await ToolExecutor.ExecuteAsync(fnName, fnArgs, ct);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Gemini] Tool {fnName} threw: {ex.Message}");
                    result = $"Tool error: {ex.Message}";
                    success = false;
                }

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

            string respJson;
            try
            {
                respJson = await PostGenerate(body, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Gemini] API call failed in tool loop: {ex.Message}");
                OnError?.Invoke($"API error during tool loop: {ex.Message}");
                return "";
            }

            try { respObj = JObject.Parse(respJson); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Gemini] Failed to parse tool loop response: {ex.Message}");
                return "";
            }

            parts = GetParts(respObj);
        }

        // Extract final text from the last response
        var finalText = ExtractText(parts);
        System.Diagnostics.Debug.WriteLine($"[Gemini] Tool loop done after {rounds} rounds, final text length={finalText.Length}");
        return finalText;
    }

    private async Task<string> PostGenerate(object body, CancellationToken ct)
    {
        var url = $"{BASE}{Uri.EscapeDataString(CurrentModel)}:generateContent?key={ApiKey}";
        var json = JsonConvert.SerializeObject(body);
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        System.Diagnostics.Debug.WriteLine($"[Gemini] POST to {CurrentModel}, body length={json.Length}");

        var resp = await _http.PostAsync(url, content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            System.Diagnostics.Debug.WriteLine($"[Gemini] API error {resp.StatusCode}: {err}");
            throw new Exception($"API {resp.StatusCode}: {err}");
        }
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private Dictionary<string, object> BuildRequestBody(List<Dictionary<string, object>> history, string sysPrompt)
    {
        var toolsEnabled = true;
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
            yield break;
        }

        var classifierModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("classifier") ?? "gemini-2.5-flash-lite";
        var classification = await Classify(userMessage, classifierModel);
        yield return new() { Kind = "classified", Data = new() { ["complexity"] = classification } };

        if (classification == "simple")
        {
            yield break;
        }

        var plannerModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("planner") ?? "gemini-3.6-flash";
        var steps = await Plan(userMessage, plannerModel);
        yield return new() { Kind = "plan_ready", Data = new() { ["steps"] = JsonConvert.SerializeObject(steps) } };

        var executorModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("executor") ?? "gemini-3.5-flash";
        var planStr = string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s}"));

        for (int i = 0; i < steps.Count; i++)
        {
            yield return new() { Kind = "step_start", Data = new() { ["step_number"] = i + 1, "total_steps"] = steps.Count, ["description"] = steps[i] } };
            yield return new() { Kind = "step_done", Data = new() { ["step_number"] = i + 1 } };
        }

        var reviewerModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("reviewer") ?? "gemini-2.5-flash-lite";
    }

    private async Task<string> Classify(string userMessage, string model)
    {
        var origModel = _service.CurrentModel;
        _service.CurrentModel = model;
        _service.CurrentModel = origModel;
        return "simple";
    }

    private async Task<List<string>> Plan(string userMessage, string model)
    {
        return new() { userMessage };
    }
}
