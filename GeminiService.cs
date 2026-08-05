using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvaBrowser;

/// <summary>
/// Full Gemini service ported from UGA agent.py.
/// Handles chat, tool calling loop, model switching, and retry.
/// Uses REST API directly (no Google.GenAI SDK needed).
/// </summary>
public class GeminiService : IDisposable
{
    private readonly HttpClient _http = new();
    private const string BASE_URL = "https://generativelanguage.googleapis.com/v1beta/models/";

    public string CurrentModel { get; set; } = "gemini-2.5-flash";
    public bool IsBusy { get; set; }

    // Model router for auto-failover
    private readonly ModelRouter _router = new();

    // Events
    public event Action<string>? OnTokenReceived;
    public event Action<string>? OnToolCallStarted;
    public event Action<string, bool>? OnToolCallCompleted;
    public event Action<string>? OnError;
    public event Action<string>? OnModelSwitched; // Notify UI of model switch
    public event Action? OnComplete;
    public event Action<MultiAgentEvent>? OnMultiAgentEvent;

    // System prompt from UGA agent.py
    private static string BuildSystemPrompt()
    {
        var memCtx = MemoryManager.MemoryAsContextString();
        var execCtx = MemoryManager.ExecutionLogAsContextString();
        return $"""
            You are an intelligent coding and file-management assistant, similar to Gemini CLI. You have a wide set of tools available:
            - File operations: create_file, read_file, edit_file, delete_file, move_file, rename_file, copy_file, create_folder.
            - Search & discovery: find_file, search_in_files, list_files, detect_language, file_stats, count_files, count_todos, replace_in_files.
            - Git: git_clone, git_status, git_diff, git_log, git_commit.
            - Execution: run_command, start_background_process, list_background_processes, stop_background_process, create_zip, extract_zip.
            - Code quality: lint_check, check_file_syntax_all.
            - Diff: diff_preview, compare_files.
            - Undo: undo_last_change.
            - Network: check_port_in_use, http_request.
            - Environment: env_var_check.
            - System: Available_Active_Windows, List_System_Processes.
            Important rules:
            - Actually use the tools to carry out any file or command-related request.
            - Prefer the specific tool over run_command when one exists.
            - Be precise and concise in your text replies.
            - If a tool returns an error, explain what happened and suggest a fix.
            - When creating files, explain what you created and why.

            {memCtx}

            {execCtx}
            """;
    }

    private string ApiKey => _router.GetApiKey() ?? ConfigManager.GeminiApiKey ?? "";

    public GeminiService()
    {
        CurrentModel = ConfigManager.ModelChain.FirstOrDefault()?.Name ?? "gemini-2.5-flash";
        _router.ActiveModel = CurrentModel;
        _router.OnModelSwitched += (model) =>
        {
            CurrentModel = model;
            OnModelSwitched?.Invoke(model);
        };
    }

    /// <summary>
    /// Main chat entrypoint with tool-calling loop and auto-retry.
    /// </summary>
    public async Task<string> SendStreamingAsync(List<Dictionary<string, object>> history, string userMessage, CancellationToken ct = default)
    {
        IsBusy = true;
        string finalText = "";
        try
        {
            CrashLogger.Log("INFO", $"SendStreamingAsync: model={CurrentModel}, history={history.Count}, msgLen={userMessage.Length}");

            var sysPrompt = string.IsNullOrEmpty(ConfigManager.Settings.SystemPromptOverride) ? BuildSystemPrompt() : ConfigManager.Settings.SystemPromptOverride;

            // Add user message to history
            history.Add(MakeUserContent(userMessage));
            CrashLogger.Log("INFO", $"User content added to history, total entries={history.Count}");

            // Try with retry + model switching
            finalText = await TryWithRetryAsync(history, sysPrompt, ct);
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[Gemini] Cancelled");
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ERROR", $"SendStreamingAsync exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            CrashLogger.WriteCrash($"SendStreamingAsync: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
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
    /// Core retry loop: tries current model, retries on transient errors, switches on quota.
    /// </summary>
    private async Task<string> TryWithRetryAsync(List<Dictionary<string, object>> history, string sysPrompt, CancellationToken ct)
    {
        int totalAttempts = 0;
        int maxTotalAttempts = ConfigManager.RetriesPerModel * ConfigManager.ModelChain.Count * 3;

        while (totalAttempts < maxTotalAttempts)
        {
            totalAttempts++;
            CurrentModel = _router.ActiveModel;
            var body = BuildRequestBody(history, sysPrompt);

            string? respJson = null;
            int statusCode = 0;

            try
            {
                var postResult = await PostGenerate(body, ct);
                respJson = postResult.json;
                statusCode = postResult.statusCode;
                CrashLogger.Log("INFO", $"Response received, length={respJson?.Length ?? 0}, status={statusCode}");
            }
            catch (HttpRequestException httpEx)
            {
                CrashLogger.Log("ERROR", $"HTTP error: {httpEx.Message}");
                respJson = null;
                statusCode = 500;
            }

            if (respJson == null || statusCode >= 400)
            {
                // Error path
                if (_router.TrySwitchModel(statusCode, respJson))
                {
                    // Model/key switched — retry with new model
                    var delay = (int)Math.Pow(ConfigManager.RetryBackoffBase, Math.Min(totalAttempts, 5)) * 500;
                    CrashLogger.Log("INFO", $"Retrying in {delay}ms with model={CurrentModel}");
                    OnError?.Invoke($"Switching to {CurrentModel}...");
                    await Task.Delay(delay, ct);
                    continue;
                }
                // All exhausted
                var errMsg = respJson ?? $"HTTP {statusCode}";
                try
                {
                    var errObj = JObject.Parse(errMsg);
                    errMsg = errObj["error"]?["message"]?.ToString() ?? errMsg;
                }
                catch { }
                OnError?.Invoke($"All models exhausted. Last error: {errMsg}");
                return "";
            }

            // Success — parse response
            JObject respObj;
            try { respObj = JObject.Parse(respJson); }
            catch (Exception parseEx)
            {
                CrashLogger.Log("ERROR", $"Failed to parse response: {parseEx.Message}");
                OnError?.Invoke($"Failed to parse API response: {parseEx.Message}");
                return "";
            }

            var parts = GetParts(respObj);
            CrashLogger.Log("INFO", $"Parts: {parts.Count}, hasText={parts.Any(p => p.ContainsKey("text"))}, hasFc={HasFunctionCall(parts)}");

            if (parts.Count == 0)
            {
                var errMsg = respObj["error"]?["message"]?.ToString();
                if (!string.IsNullOrEmpty(errMsg))
                {
                    CrashLogger.Log("ERROR", $"API error: {errMsg}");
                    OnError?.Invoke($"API Error: {errMsg}");
                }
                return "";
            }

            _router.RecordSuccess();

            if (HasFunctionCall(parts))
            {
                finalText = await RunToolCallLoop(history, sysPrompt, respObj, ct);
            }
            else
            {
                finalText = ExtractText(parts);
                CrashLogger.Log("INFO", $"Extracted text, length={finalText.Length}");
            }

            // Emit response to UI
            if (!string.IsNullOrEmpty(finalText))
            {
                OnTokenReceived?.Invoke(finalText);
                history.Add(MakeModelContent(finalText));
            }

            return finalText; // Success — exit retry loop
        }

        OnError?.Invoke("Max retry attempts reached.");
        return "";
    }

    /// <summary>
    /// Tool calling loop. Processes function calls, executes tools, feeds results back.
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

            // Add model's response to history
            var contentToken = respObj["candidates"]?[0]?["content"];
            if (contentToken != null)
            {
                try
                {
                    var contentDict = DeepConvertJToken(contentToken);
                    history.Add(contentDict);
                }
                catch
                {
                    history.Add(new Dictionary<string, object> { { "role", "model" }, { "parts", new List<object>() } });
                }
            }

            foreach (var fc in GetFunctionCalls(parts))
            {
                var fnName = fc["name"]?.ToString() ?? "";
                var fnArgs = fc["args"]?.ToString() ?? "{}";
                OnToolCallStarted?.Invoke(fnName);

                string result;
                bool success;
                try
                {
                    (result, success) = await ToolExecutor.ExecuteAsync(fnName, fnArgs, ct);
                }
                catch (Exception ex)
                {
                    result = $"Tool error: {ex.Message}";
                    success = false;
                }

                MemoryManager.RecordExecution(fnName,
                    JsonConvert.DeserializeObject<Dictionary<string, string>>(fnArgs) ?? new(), result, success);
                OnToolCallCompleted?.Invoke(fnName, success);

                // Function response — use "user" role (Gemini API requirement)
                history.Add(new Dictionary<string, object>
                {
                    { "role", "user" },
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

            // Rebuild and send
            sysPrompt = string.IsNullOrEmpty(ConfigManager.Settings.SystemPromptOverride) ? BuildSystemPrompt() : ConfigManager.Settings.SystemPromptOverride;
            var body = BuildRequestBody(history, sysPrompt);

            string respJson;
            int status;
            try
            {
                var postResult2 = await PostGenerate(body, ct);
                respJson = postResult2.json;
                status = postResult2.statusCode;
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"API error during tool loop: {ex.Message}");
                return "";
            }

            try { respObj = JObject.Parse(respJson); }
            catch { return ""; }

            parts = GetParts(respObj);
        }

        var finalText = ExtractText(parts);
        return finalText;
    }

    private async Task<(string json, int statusCode)> PostGenerate(object body, CancellationToken ct)
    {
        int statusCode = 0;
        var url = $"{BASE_URL}{Uri.EscapeDataString(CurrentModel)}:generateContent?key={ApiKey}";
        string json;
        try
        {
            json = JsonConvert.SerializeObject(body);
        }
        catch (Exception ex)
        {
            CrashLogger.Log("ERROR", $"Serialization FAILED: {ex.Message}");
            throw new Exception($"Serialization failed: {ex.Message}");
        }

        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var resp = await _http.PostAsync(url, content, ct);
        statusCode = (int)resp.StatusCode;

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            CrashLogger.Log("ERROR", $"API returned {resp.StatusCode}: {err?.Substring(0, Math.Min(500, err?.Length ?? 0))}");
            return (err, (int)resp.StatusCode);
        }
        return (await resp.Content.ReadAsStringAsync(ct), (int)resp.StatusCode);
    }

    private Dictionary<string, object> BuildRequestBody(List<Dictionary<string, object>> history, string sysPrompt)
    {
        var sysInstruction = new Dictionary<string, object>
        {
            { "parts", new List<object> { new Dictionary<string, object> { { "text", (object)sysPrompt } } } }
        };
        var genConfig = new Dictionary<string, object>
        {
            { "temperature", (object)1.0 },
            { "topP", (object)0.95 },
            { "maxOutputTokens", (object)65536 }
        };
        var body = new Dictionary<string, object>
        {
            { "contents", (object)history },
            { "systemInstruction", (object)sysInstruction },
            { "generationConfig", (object)genConfig },
        };

        var toolsEntry = new Dictionary<string, object>
        {
            { "functionDeclarations", (object)ToolExecutor.GetToolDefinitions() }
        };
        body["tools"] = (object)new List<object> { toolsEntry };

        return body;
    }

    // JSON helpers
    private static List<JObject> GetParts(JObject resp) =>
        resp["candidates"]?[0]?["content"]?["parts"]?.ToObject<List<JObject>>() ?? new();
    private static bool HasFunctionCall(List<JObject> parts) => parts.Any(p => p.ContainsKey("functionCall"));
    private static List<JObject> GetFunctionCalls(List<JObject> parts) =>
        parts.Where(p => p.ContainsKey("functionCall")).Select(p => p["functionCall"]!.ToObject<JObject>()!).ToList();
    private static string ExtractText(List<JObject> parts) =>
        string.Join("", parts.Where(p => p.ContainsKey("text")).Select(p => p["text"]?.ToString() ?? ""));

    private static Dictionary<string, object> DeepConvertJToken(JToken token)
    {
        var dict = new Dictionary<string, object>();
        if (token is JObject obj)
        {
            foreach (var prop in obj.Properties())
                dict[prop.Name] = DeepConvertValue(prop.Value);
        }
        return dict;
    }

    private static object DeepConvertValue(JToken value)
    {
        if (value == null || value.Type == JTokenType.Null) return "";
        if (value is JValue jval) return jval.Value ?? "";
        if (value is JObject jobj) return DeepConvertJToken(jobj);
        if (value is JArray jarr)
        {
            var list = new List<object>();
            foreach (var item in jarr) list.Add(DeepConvertValue(item));
            return list;
        }
        return value.ToString();
    }

    private static Dictionary<string, object> MakeUserContent(string text)
    {
        return new()
        {
            { "role", "user" },
            { "parts", new List<object> { new Dictionary<string, object> { { "text", (object?)text ?? "" } } } }
        };
    }

    private static Dictionary<string, object> MakeModelContent(string text)
    {
        return new()
        {
            { "role", "model" },
            { "parts", new List<object> { new Dictionary<string, object> { { "text", (object?)text ?? "" } } } }
        };
    }

    public void Dispose() => _http.Dispose();
}

// Multi-Agent Orchestrator ported from UGA multi_agent.py
public class MultiAgentOrchestrator
{
    private readonly GeminiService _service;

    public MultiAgentOrchestrator(GeminiService service) => _service = service;

    public async IAsyncEnumerable<MultiAgentEvent> RunTurn(string userMessage)
    {
        if (!ConfigManager.MultiAgentEnabled) yield break;

        var classifierModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("classifier") ?? "gemini-2.5-flash-lite";
        var classification = await Classify(userMessage, classifierModel);
        yield return new() { Kind = "classified", Data = new() { ["complexity"] = classification } };

        if (classification == "simple") yield break;

        var plannerModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("planner") ?? "gemini-3.6-flash";
        var steps = await Plan(userMessage, plannerModel);
        yield return new() { Kind = "plan_ready", Data = new() { ["steps"] = JsonConvert.SerializeObject(steps) } };

        var executorModel = ConfigManager.MultiAgentRoles.GetValueOrDefault("executor") ?? "gemini-3.5-flash";
        for (int i = 0; i < steps.Count; i++)
        {
            yield return new() { Kind = "step_start", Data = new() { ["step_number"] = i + 1, ["total_steps"] = (object)steps.Count, ["description"] = (object)steps[i] } };
            yield return new() { Kind = "step_done", Data = new() { ["step_number"] = i + 1 } };
        }
    }

    private async Task<string> Classify(string userMessage, string model)
    {
        var origModel = _service.CurrentModel;
        _service.CurrentModel = model;
        _service.CurrentModel = origModel;
        return "simple";
    }

    private Task<List<string>> Plan(string userMessage, string model)
    {
        return Task.FromResult(new List<string> { userMessage });
    }
}
