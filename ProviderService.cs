using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdvaBrowser;

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
}