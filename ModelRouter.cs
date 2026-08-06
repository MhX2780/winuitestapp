using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using System.Net.Http.Headers;

namespace UGA;

/// <summary>
/// Model Router ported from UGA model_router.py.
/// Handles auto-failover between models, quota detection, retry with backoff,
/// multi-key rotation, and usage stats tracking.
/// </summary>
public class ModelRouter : IDisposable
{
    private readonly HttpClient _http = new();
    private const string BASE = "https://generativelanguage.googleapis.com/v1beta/models/";

    // Events
    public event Action<string>? OnModelSwitched;
    public event Action<string, string>? OnRetry;

    // State
    public string ActiveModel { get; private set; } = "";
    public int ActiveModelIndex { get; private set; } = 0;
    public int ActiveKeyIndex { get; private set; } = 0;
    public string SwitchReason { get; private set; } = "";

    // Per-model request counters
    private readonly Dictionary<string, int> _modelRequestCounts = new();
    private readonly Dictionary<string, int> _modelFailCounts = new();

    // API keys
    private List<string> _apiKeys = new();

    public ModelRouter()
    {
        InitializeKeys();
        InitializeModel();
    }

    private void InitializeKeys()
    {
        _apiKeys = ConfigManager.LoadApiKeyPool();
        if (_apiKeys.Count == 0)
            _apiKeys = new List<string> { "" }; // placeholder
    }

    private void InitializeModel()
    {
        var chain = ConfigManager.ModelChain;
        if (chain.Count > 0)
            ActiveModel = chain[0].Name;
        else
            ActiveModel = "gemini-2.5-flash";
    }

    public string GetApiKey()
    {
        if (_apiKeys.Count == 0) return "";
        if (ActiveKeyIndex >= _apiKeys.Count) ActiveKeyIndex = 0;
        return _apiKeys[ActiveKeyIndex];
    }

    /// <summary>
    /// Determines if an error is quota-related (should switch model/key) vs transient (should retry).
    /// </summary>
    public static bool IsQuotaError(int statusCode, string? responseBody)
    {
        if (statusCode == 429) return true; // Rate limit
        if (statusCode == 403) return true; // Forbidden (often quota)
        if (statusCode == 400 && responseBody != null)
        {
            try
            {
                var obj = JObject.Parse(responseBody);
                var msg = obj["error"]?["message"]?.ToString() ?? "";
                if (msg.Contains("quota", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) return true;
                if (msg.Contains("User location is not supported", StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
        }
        return false;
    }

    public static bool IsTransientError(int statusCode, string? responseBody)
    {
        return statusCode >= 500 || statusCode == 429;
    }

    /// <summary>
    /// Attempts to switch to the next available model/key combination.
    /// Returns true if a switch was made, false if all exhausted.
    /// </summary>
    public bool TrySwitchModel(int statusCode, string? responseBody)
    {
        var chain = ConfigManager.ModelChain;
        if (IsQuotaError(statusCode, responseBody))
        {
            // Try next model in chain
            for (int i = 0; i < chain.Count; i++)
            {
                var nextIdx = (ActiveModelIndex + 1 + i) % chain.Count;
                var nextModel = chain[nextIdx].Name;

                // Check per-model session cap
                if (chain[nextIdx].MaxRequestsPerSession.HasValue)
                {
                    var count = _modelRequestCounts.GetValueOrDefault(nextModel, 0);
                    if (count >= chain[nextIdx].MaxRequestsPerSession.Value)
                        continue; // Skip exhausted model
                }

                ActiveModelIndex = nextIdx;
                ActiveModel = nextModel;
                SwitchReason = $"Quota/limit on previous model (HTTP {statusCode})";
                CrashLogger.Log("INFO", $"Model switched to {ActiveModel}: {SwitchReason}");
                OnModelSwitched?.Invoke(ActiveModel);
                return true;
            }

            // All models exhausted for this key — try next key
            if (_apiKeys.Count > 1)
            {
                ActiveKeyIndex = (ActiveKeyIndex + 1) % _apiKeys.Count;
                ActiveModelIndex = 0;
                ActiveModel = chain[0].Name;
                SwitchReason = $"All models exhausted for key, switching to key #{ActiveKeyIndex + 1}";
                CrashLogger.Log("INFO", $"Key switched to #{ActiveKeyIndex + 1}: {SwitchReason}");
                OnModelSwitched?.Invoke(ActiveModel);
                return true;
            }

            return false; // All models and keys exhausted
        }

        // Non-quota error — just increment failure count
        _modelFailCounts[ActiveModel] = _modelFailCounts.GetValueOrDefault(ActiveModel, 0) + 1;

        // If same model failed 3+ times consecutively, try next model
        if (_modelFailCounts[ActiveModel] >= 3 && chain.Count > 1)
        {
            var nextIdx = (ActiveModelIndex + 1) % chain.Count;
            ActiveModelIndex = nextIdx;
            ActiveModel = chain[nextIdx].Name;
            _modelFailCounts[ActiveModel] = 0;
            SwitchReason = $"Model {chain[ActiveModelIndex].Name} failed {_modelFailCounts.GetValueOrDefault(chain[nextIdx].Name, 0)} times";
            CrashLogger.Log("INFO", $"Model switched to {ActiveModel}: {SwitchReason}");
            OnModelSwitched?.Invoke(ActiveModel);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Records a successful request for the active model.
    /// </summary>
    public void RecordSuccess()
    {
        _modelRequestCounts[ActiveModel] = _modelRequestCounts.GetValueOrDefault(ActiveModel, 0) + 1;
        _modelFailCounts[ActiveModel] = 0; // Reset failure streak

        // Check if we've hit the per-model session cap
        var chain = ConfigManager.ModelChain;
        if (ActiveModelIndex < chain.Count && chain[ActiveModelIndex].MaxRequestsPerSession.HasValue)
        {
            if (_modelRequestCounts[ActiveModel] >= chain[ActiveModelIndex].MaxRequestsPerSession.Value)
            {
                CrashLogger.Log("INFO", $"Model {ActiveModel} reached session cap, switching");
                TrySwitchModel(429, "Session request limit reached");
            }
        }
    }

    /// <summary>
    /// Gets the current chain info for display.
    /// </summary>
    public string GetStatus()
    {
        var chain = ConfigManager.ModelChain;
        var lines = new List<string>
        {
            $"Active: {ActiveModel} (key #{ActiveKeyIndex + 1}/{_apiKeys.Count})",
            $"Requests: {_modelRequestCounts.GetValueOrDefault(ActiveModel, 0)}",
            $"Chain position: {ActiveModelIndex + 1}/{chain.Count}",
        };
        if (!string.IsNullOrEmpty(SwitchReason))
            lines.Add($"Last switch: {SwitchReason}");
        return string.Join("\n", lines);
    }

    public void Reset()
    {
        _modelRequestCounts.Clear();
        _modelFailCounts.Clear();
        ActiveModelIndex = 0;
        ActiveKeyIndex = 0;
        InitializeModel();
    }

    public void Dispose() => _http.Dispose();
}
