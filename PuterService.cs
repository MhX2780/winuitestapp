using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvaBrowser;

/// <summary>
/// Full Puter.js REST API integration ported from UGA providers.py.
/// Uses Puter's OpenAI-compatible endpoint (api.puter.com/puterai/openai/v1/)
/// for chat completions, tool calling, vision, and image generation.
/// 
/// Puter.js gives free access to 500+ models (Claude, GPT, DeepSeek, etc.)
/// under a "User-Pays" model — each user authenticates their own Puter account.
/// Token obtained from puter.com/dashboard#account → "Create token".
/// </summary>
public class PuterService : IDisposable
{
    private const string PUTER_BASE_URL = "https://api.puter.com/puterai/openai/v1";
    private const string PUTER_MODELS_ENDPOINT = "https://api.puter.com/puterai/chat/models/details";

    private readonly HttpClient _http;

    public PuterService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetToken());
    }

    private static string GetToken()
    {
        return ConfigManager.LoadPuterToken();
    }

    /// <summary>
    /// Refresh auth header when token changes (e.g. user saves new token in Settings).
    /// </summary>
    public void RefreshToken()
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetToken());
    }

    // ─── Free-only enforcement (from providers._enforce_puter_free_only) ───

    private static void EnforceFreeOnly(string modelName)
    {
        if (ConfigManager.Settings.PuterFreeOnly && !modelName.ToLowerInvariant().Contains("free"))
            throw new InvalidOperationException(
                $"'{modelName}' is blocked by the \"Use Free Models Only (Puter.js)\" setting " +
                "(model id doesn't contain \"free\"). Pick a free model or turn the setting off.");
    }

    // ─── Deep Thinking kwargs (from providers._puter_reasoning_kwargs) ───

    private static JObject BuildReasoningKwargs()
    {
        var kwargs = new JObject();
        if (ConfigManager.Settings.PuterDeepThinkingEnabled)
            kwargs["reasoning_effort"] = ConfigManager.Settings.PuterDeepThinkingEffort;
        return kwargs;
    }

    // ─── Plain text generation (from providers._puter_generate) ───

    /// <summary>
    /// Sends a simple text-in/text-out request to Puter.
    /// Used for multi-agent roles (classifier, planner, reviewer) when routed to Puter.
    /// </summary>
    public async Task<string> GenerateAsync(string model, string prompt, string? systemInstruction = null, CancellationToken ct = default)
    {
        EnforceFreeOnly(model);
        var token = GetToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Puter auth token configured. Get one at puter.com/dashboard#account.");

        var messages = new JArray();
        if (!string.IsNullOrEmpty(systemInstruction))
            messages.Add(new JObject { ["role"] = "system", ["content"] = systemInstruction });
        messages.Add(new JObject { ["role"] = "user", ["content"] = prompt });

        var body = new JObject
        {
            ["model"] = model,
            ["messages"] = messages,
        };
        // Merge reasoning kwargs
        foreach (var kv in BuildReasoningKwargs())
            body[kv.Key] = kv.Value;

        var resp = await _http.PostAsync($"{PUTER_BASE_URL}/chat/completions",
            new StringContent(body.ToString(), Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Puter API error {resp.StatusCode}: {json}");

        var obj = JObject.Parse(json);
        return obj["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
    }

    // ─── Streaming text generation (from providers._puter_generate_stream) ───

    /// <summary>
    /// Streaming text generation. Yields text chunks via callback.
    /// </summary>
    public async Task StreamAsync(string model, string prompt, string? systemInstruction,
        Action<string> onText, CancellationToken ct = default)
    {
        EnforceFreeOnly(model);
        var token = GetToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Puter auth token configured.");

        var messages = new JArray();
        if (!string.IsNullOrEmpty(systemInstruction))
            messages.Add(new JObject { ["role"] = "system", ["content"] = systemInstruction });
        messages.Add(new JObject { ["role"] = "user", ["content"] = prompt });

        var body = new JObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
        };
        foreach (var kv in BuildReasoningKwargs())
            body[kv.Key] = kv.Value;

        var resp = await _http.PostAsync($"{PUTER_BASE_URL}/chat/completions",
            new StringContent(body.ToString(), Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;
            try
            {
                var chunk = JObject.Parse(data);
                var delta = chunk["choices"]?[0]?["delta"]?["content"]?.ToString();
                if (!string.IsNullOrEmpty(delta))
                    onText(delta);
            }
            catch { /* skip malformed chunks */ }
        }
    }

    // ─── Chat with tools - non-streaming (from providers.puter_chat_with_tools) ───

    /// <summary>
    /// Sends an OpenAI-style messages list (with prior tool_calls and tool-role results)
    /// to Puter, optionally with a tools schema. Returns the raw response JObject.
    /// The caller inspects choices[0].message for .content and .tool_calls.
    /// </summary>
    public async Task<JObject> ChatWithToolsAsync(string model, JArray messages, JArray? tools = null, CancellationToken ct = default)
    {
        EnforceFreeOnly(model);
        var token = GetToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Puter auth token configured.");

        var body = new JObject
        {
            ["model"] = model,
            ["messages"] = messages,
        };
        if (tools != null && tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        foreach (var kv in BuildReasoningKwargs())
            body[kv.Key] = kv.Value;

        var resp = await _http.PostAsync($"{PUTER_BASE_URL}/chat/completions",
            new StringContent(body.ToString(), Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Puter API error {resp.StatusCode}: {json}");

        return JObject.Parse(json);
    }

    // ─── Chat with tools - streaming (from providers.puter_chat_with_tools_stream) ───

    /// <summary>
    /// Streaming chat with tool-calling support. Yields events:
    ///   {"type": "text", "text": "..."} - a text chunk to display
    ///   {"type": "tool_calls", "tool_calls": [...]} - fully assembled tool calls (once, after stream ends)
    /// 
    /// Tool call fragments (name/arguments split across chunks) are reassembled
    /// by their index before being yielded.
    /// </summary>
    public async Task StreamWithToolsAsync(string model, JArray messages, JArray? tools,
        Action<string> onText, Action<JArray> onToolCalls, CancellationToken ct = default)
    {
        EnforceFreeOnly(model);
        var token = GetToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Puter auth token configured.");

        var body = new JObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = true,
        };
        if (tools != null && tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }
        foreach (var kv in BuildReasoningKwargs())
            body[kv.Key] = kv.Value;

        var resp = await _http.PostAsync($"{PUTER_BASE_URL}/chat/completions",
            new StringContent(body.ToString(), Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();

        // Accumulates partial tool-call fragments keyed by index
        var fragments = new Dictionary<int, Dictionary<string, string>>();

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;
            try
            {
                var chunk = JObject.Parse(data);
                var choices = chunk["choices"];
                if (choices == null || !choices.HasValues) continue;
                var delta = choices[0]?["delta"];

                // Text content
                var content = delta?["content"]?.ToString();
                if (!string.IsNullOrEmpty(content))
                    onText(content);

                // Tool call deltas
                var toolCalls = delta?["tool_calls"];
                if (toolCalls != null)
                {
                    foreach (var tcDelta in toolCalls)
                    {
                        var idx = tcDelta["index"]?.Value<int>() ?? 0;
                        if (!fragments.ContainsKey(idx))
                            fragments[idx] = new() { ["id"] = "", ["name"] = "", ["arguments"] = "" };

                        var entry = fragments[idx];
                        var id = tcDelta["id"]?.ToString();
                        if (!string.IsNullOrEmpty(id)) entry["id"] = id;
                        var fn = tcDelta["function"];
                        if (fn != null)
                        {
                            var fnName = fn["name"]?.ToString();
                            if (!string.IsNullOrEmpty(fnName)) entry["name"] += fnName;
                            var fnArgs = fn["arguments"]?.ToString();
                            if (!string.IsNullOrEmpty(fnArgs)) entry["arguments"] += fnArgs;
                        }
                    }
                }
            }
            catch { /* skip malformed chunks */ }
        }

        // Yield assembled tool calls if any
        if (fragments.Count > 0)
        {
            var assembled = new JArray();
            foreach (var kv in fragments.OrderBy(k => k.Key))
            {
                assembled.Add(new JObject
                {
                    ["id"] = kv.Value["id"],
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = kv.Value["name"],
                        ["arguments"] = kv.Value["arguments"],
                    }
                });
            }
            onToolCalls(assembled);
        }
    }

    // ─── Vision describe (from providers.puter_vision_describe) ───

    /// <summary>
    /// BETA. Sends an image + question to a Puter-hosted vision-capable model
    /// via chat.completions with an image_url content part (data: URI).
    /// Used by Image_Fetch_Puter and view_screen_puter tools.
    /// </summary>
    public async Task<string> VisionDescribeAsync(string model, byte[] imageBytes, string mimeType, string question, CancellationToken ct = default)
    {
        EnforceFreeOnly(model);
        var token = GetToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Puter auth token configured.");

        var b64 = Convert.ToBase64String(imageBytes);
        var dataUri = $"data:{mimeType};base64,{b64}";

        var body = new JObject
        {
            ["model"] = model,
            ["messages"] = new JArray
            {
                new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject { ["type"] = "text", ["text"] = question },
                        new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = dataUri } },
                    }
                }
            }
        };

        var resp = await _http.PostAsync($"{PUTER_BASE_URL}/chat/completions",
            new StringContent(body.ToString(), Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Puter vision error {resp.StatusCode}: {json}");

        var obj = JObject.Parse(json);
        return obj["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
    }

    // ─── Image generation - best effort (from providers.puter_image_generate) ───

    /// <summary>
    /// BETA — genuinely unverified. Attempts image generation through Puter
    /// via the OpenAI images.generate() endpoint. Puter's own docs only show
    /// image generation via browser JS SDK (puter.ai.txt2img()), no REST route.
    /// This may fail with 404 — handled by the caller (Image_Create_Puter tool).
    /// Returns raw image bytes on success.
    /// </summary>
    public async Task<byte[]> ImageGenerateAsync(string model, string prompt, CancellationToken ct = default)
    {
        EnforceFreeOnly(model);
        var token = GetToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Puter auth token configured.");

        var body = new JObject
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["response_format"] = "b64_json",
        };

        var resp = await _http.PostAsync($"{PUTER_BASE_URL}/images/generations",
            new StringContent(body.ToString(), Encoding.UTF8, "application/json"), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Puter image generation error {resp.StatusCode}: {json}");

        var obj = JObject.Parse(json);
        var b64Data = obj["data"]?[0]?["b64_json"]?.ToString();
        if (string.IsNullOrEmpty(b64Data))
            throw new InvalidOperationException("Puter's image endpoint returned no image data.");

        return Convert.FromBase64String(b64Data);
    }

    // ─── List models (from providers.puter_list_models) ───

    /// <summary>
    /// Fetches available Puter models. Note: Puter does NOT implement /v1/models —
    /// uses its own endpoint instead (PUTER_MODELS_ENDPOINT).
    /// Response shape varies: bare list, {"models": [...]}, or {"data": [...]}.
    /// </summary>
    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var token = GetToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("No Puter auth token configured.");

        var request = new HttpRequestMessage(HttpMethod.Get, PUTER_MODELS_ENDPOINT);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await _http.SendAsync(request, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();

        var data = JToken.Parse(json);
        JArray? entries = null;

        if (data is JObject obj)
            entries = obj["models"] as JArray ?? obj["data"] as JArray;
        else if (data is JArray arr)
            entries = arr;

        var modelIds = new List<string>();
        if (entries != null)
        {
            foreach (var entry in entries)
            {
                if (entry.Type == JTokenType.String)
                    modelIds.Add(entry.ToString());
                else if (entry is JObject eo)
                {
                    var id = eo["id"]?.ToString() ?? eo["name"]?.ToString();
                    if (!string.IsNullOrEmpty(id)) modelIds.Add(id);
                }
            }
        }
        return modelIds;
    }

    /// <summary>
    /// Lists only free-tier models (IDs containing "free").
    /// </summary>
    public async Task<List<string>> ListFreeModelsAsync(CancellationToken ct = default)
    {
        var all = await ListModelsAsync(ct);
        return all.Where(m => m.ToLowerInvariant().EndsWith(":free") ||
                              m.ToLowerInvariant().EndsWith("-free") ||
                              m.ToLowerInvariant().EndsWith("_free") ||
                              m.ToLowerInvariant().Contains(":free"))
                  .ToList();
    }

    // ─── Tool schema builder (from tool_schemas.py) ───

    /// <summary>
    /// Builds an OpenAI-style tool schema for one tool, matching the format
    /// that Puter's tool-calling endpoint expects.
    /// Ported from tool_schemas.tool_to_openai_schema.
    /// </summary>
    public static JObject BuildToolSchema(string name, string description, Dictionary<string, object> parameters)
    {
        return new JObject
        {
            ["type"] = "function",
            ["function"] = new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = JObject.FromObject(parameters),
            }
        };
    }

    /// <summary>
    /// Builds the full tools array for all registered tools in ToolExecutor.
    /// </summary>
    public static JArray BuildAllToolsSchema()
    {
        var schemas = new JArray();
        foreach (var decl in ToolExecutor.GetToolDeclarations())
        {
            try
            {
                schemas.Add(BuildToolSchema(
                    decl.Name,
                    decl.Description,
                    new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = decl.Properties,
                        ["required"] = decl.Required,
                    }
                ));
            }
            catch { /* skip malformed tools */ }
        }
        return schemas;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
