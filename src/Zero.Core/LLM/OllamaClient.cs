using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zero.Core.LLM;

public sealed class OllamaClient
{
    private readonly HttpClient       _http;
    private readonly ILogger<OllamaClient> _log;
    private readonly ZeroConfig       _cfg;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OllamaClient(HttpClient http, IOptions<ZeroConfig> cfg, ILogger<OllamaClient> log)
    {
        _http = http;
        _cfg  = cfg.Value;
        _log  = log;
    }

    /// <summary>Returns true if Ollama is reachable.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/tags", ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Send a chat request to Ollama and return the response message.
    /// Tool definitions are optional; pass null for plain chat.
    /// </summary>
    public async Task<ChatMessage?> ChatAsync(
        List<ChatMessage> messages,
        List<OllamaTool>?  tools           = null,
        bool               enableThinking  = false,
        string?            modelName       = null,
        CancellationToken  ct              = default)
    {
        var request = new ChatRequest
        {
            Model    = modelName ?? _cfg.ModelName,
            Messages = messages,
            Tools    = tools,
            Stream   = false,
            Think    = enableThinking ? true : null,
            Options  = _cfg.OllamaNumGpu.HasValue
                ? new OllamaOptions { NumGpu = _cfg.OllamaNumGpu.Value }
                : null
        };

        _log.LogDebug("Sending chat request to Ollama. Model={Model} Messages={Count} Tools={Tools}",
            request.Model, messages.Count, tools?.Count ?? 0);

        using var response = await _http.PostAsJsonAsync("/api/chat", request, _json, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _log.LogError("Ollama returned {StatusCode}: {Body}", (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(_json, ct);

        _log.LogDebug("Ollama response received. Done={Done}", result?.Done);
        return result?.Message;
    }
}
