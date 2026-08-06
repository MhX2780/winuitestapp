using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UGA;

/// <summary>
/// Multi-provider abstraction ported from UGA providers.py.
/// Supports Claude (Anthropic), OpenAI, and Puter.js for plain text generation.
/// Tool calling only supported via Gemini (see GeminiService.cs).
/// All providers are optional - only loaded when configured.
/// </summary>
public static class ProviderService
{
    private static readonly HttpClient _http = new();

    // Claude (Anthropic)
    public static async Task<string?> ClaudeGenerate(string model, string systemPrompt, List<Dictionary<string, string>> messages)
    {
        var apiKey = ConfigManager.LoadProviderApiKey("claude");
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "max_tokens", 8192 },
                { "system", systemPrompt },
                { "messages", messages.Select(m => new { role = m["role"], content = m["content"] }).ToList() },
            };

            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(JsonConvert.SerializeObject(body));
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["content"]?[0]?["text"]?.ToString();
        }
        catch { return null; }
    }

    // OpenAI
    public static async Task<string?> OpenAIGenerate(string model, string systemPrompt, List<Dictionary<string, string>> messages)
    {
        var apiKey = ConfigManager.LoadProviderApiKey("openai");
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            var allMessages = new List<Dictionary<string, string>>
            {
                new() { { "role", "system" }, { "content", systemPrompt } }
            };
            allMessages.AddRange(messages);

            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "max_tokens", 8192 },
                { "messages", allMessages.Select(m => new { role = m["role"], content = m["content"] }).ToList() },
            };

            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            req.Headers.Add("Authorization", $"Bearer {apiKey}");
            req.Content = new StringContent(JsonConvert.SerializeObject(body));
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["choices"]?[0]?["message"]?["content"]?.ToString();
        }
        catch { return null; }
    }

    // Puter.js
    public static async Task<string?> PuterChat(string model, string systemPrompt, List<Dictionary<string, string>> messages)
    {
        var token = ConfigManager.LoadPuterToken();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            var allMessages = new List<Dictionary<string, string>>
            {
                new() { { "role", "system" }, { "content", systemPrompt } }
            };
            allMessages.AddRange(messages);

            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "messages", allMessages.Select(m => new { role = m["role"], content = m["content"] }).ToList() },
            };

            var req = new HttpRequestMessage(HttpMethod.Post, "https://api.puter.com/puterai/openai/v1/chat/completions");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Content = new StringContent(JsonConvert.SerializeObject(body));
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["choices"]?[0]?["message"]?["content"]?.ToString();
        }
        catch { return null; }
    }

    // Generic provider call
    public static async Task<string?> GenerateForProvider(string providerId, string model, string systemPrompt, List<Dictionary<string, string>> messages)
    {
        return providerId switch
        {
            "claude" => await ClaudeGenerate(model, systemPrompt, messages),
            "openai" => await OpenAIGenerate(model, systemPrompt, messages),
            "puter" => await PuterChat(model, systemPrompt, messages),
            _ => null,
        };
    }

    // ─── Puter.js Tool Calling (BETA) ───

    /// <summary>
    /// Sends a chat completion request to Puter with optional tool definitions.
    /// Returns the response text, or null on failure.
    /// </summary>
    public static async Task<(string? text, List<PuterToolCall>? toolCalls)> PuterChatWithTools(
        string model, List<Dictionary<string, object>> messages, List<Dictionary<string, object>>? tools = null)
    {
        var token = ConfigManager.LoadPuterToken();
        if (string.IsNullOrEmpty(token))
            return (null, null);

        try
        {
            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "messages", messages },
            };
            if (tools != null && tools.Count > 0)
            {
                body["tools"] = tools.Select(t => new { type = "function", @function = t }).ToList();
                body["tool_choice"] = "auto";
            }

            // Deep thinking support
            if (ConfigManager.Settings.PuterDeepThinkingEnabled)
            {
                body["reasoning_effort"] = ConfigManager.Settings.PuterDeepThinkingEffort;
            }

            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.puter.com/puterai/openai/v1/chat/completions");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());

            var text = json["choices"]?[0]?["message"]?["content"]?.ToString();

            // Parse tool calls from response
            List<PuterToolCall>? tcList = null;
            var toolCallsArray = json["choices"]?[0]?["message"]?["tool_calls"];
            if (toolCallsArray != null && toolCallsArray.HasValues)
            {
                tcList = new List<PuterToolCall>();
                foreach (var tc in toolCallsArray)
                {
                    tcList.Add(new PuterToolCall
                    {
                        Id = tc["id"]?.ToString() ?? "",
                        Name = tc["function"]?["name"]?.ToString() ?? "",
                        Arguments = tc["function"]?["arguments"]?.ToString() ?? "{}",
                    });
                }
            }

            return (text, tcList);
        }
        catch { return (null, null); }
    }

    // ─── Puter.js Vision (BETA) ───

    /// <summary>
    /// Sends an image + question to Puter's vision-capable model.
    /// Used by Image_Fetch_Puter and view_screen_puter tools.
    /// </summary>
    public static async Task<string?> PuterVisionDescribe(string model, byte[] imageBytes, string mimeType, string question)
    {
        var token = ConfigManager.LoadPuterToken();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            var b64 = Convert.ToBase64String(imageBytes);
            var dataUri = $@"data:{mimeType};base64,{b64}";

            var messages = new List<Dictionary<string, object>>
            {
                new() { { "role", "user" }, { "content", new object[] {
                    new { type = "text", text = question },
                    new { type = "image_url", image_url = new { url = dataUri } },
                }}},
            };

            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "messages", messages },
            };

            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.puter.com/puterai/openai/v1/chat/completions");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["choices"]?[0]?["message"]?["content"]?.ToString();
        }
        catch { return null; }
    }

    // ─── Puter.js Image Generation (BETA - unverified) ───

    /// <summary>
    /// Attempts image generation through Puter via images.generate endpoint.
    /// BETA - may not work as Puter only officially supports browser SDK for image gen.
    /// Returns image bytes on success, null on failure.
    /// </summary>
    public static async Task<byte[]?> PuterImageGenerate(string model, string prompt)
    {
        var token = ConfigManager.LoadPuterToken();
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "prompt", prompt },
                { "response_format", "b64_json" },
            };

            var req = new HttpRequestMessage(HttpMethod.Post,
                "https://api.puter.com/puterai/openai/v1/images/generations");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            var b64Data = json["data"]?[0]?["b64_json"]?.ToString();
            if (string.IsNullOrEmpty(b64Data)) return null;
            return Convert.FromBase64String(b64Data);
        }
        catch { return null; }
    }

    // ─── Puter.js Model Listing ───

    /// <summary>
    /// Fetches available models from Puter.js via their models endpoint.
    /// </summary>
    public static async Task<List<string>> PuterListModels()
    {
        var token = ConfigManager.LoadPuterToken();
        if (string.IsNullOrEmpty(token)) return new();

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.puter.com/puterai/chat/models/details");
            req.Headers.Add("Authorization", $"Bearer {token}");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());

            var modelIds = new List<string>();
            JToken? entries = json["models"] ?? json["data"];
            if (entries == null && json.Type == JTokenType.Array)
                entries = json;

            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (entry.Type == JTokenType.String)
                        modelIds.Add(entry.ToString());
                    else if (entry.Type == JTokenType.Object)
                    {
                        var id = entry["id"]?.ToString() ?? entry["name"]?.ToString();
                        if (!string.IsNullOrEmpty(id)) modelIds.Add(id);
                    }
                }
            }
            return modelIds;
        }
        catch { return new(); }
    }

    /// <summary>
    /// Lists only free-tier Puter models (containing ':free' in their ID).
    /// </summary>
    public static async Task<List<string>> PuterListFreeModels()
    {
        var all = await PuterListModels();
        return all.Where(m => m.ToLower().Contains(":free")
                             || m.ToLower().EndsWith("-free")
                             || m.ToLower().EndsWith("_free")).ToList();
    }
}

/// <summary>
/// Represents a tool call returned by Puter's function-calling endpoint.
/// </summary>
public class PuterToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}