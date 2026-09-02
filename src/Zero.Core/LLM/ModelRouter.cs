using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zero.Core.LLM;

/// <summary>
/// Rule-based model router. Classifies each user input into a tier and returns
/// the appropriate Ollama model name to use for that turn.
///
/// Tiers:
///   FAST   → qwen3:1.7b  — simple/short commands, chitchat, OS actions
///   MAIN   → qwen3:8b    — complex reasoning, coding, multi-step tasks (fallback)
///   VISION → qwen3-vl:8b — image context (when images are attached)
/// </summary>
public sealed class ModelRouter
{
    private readonly ZeroConfig            _cfg;
    private readonly ILogger<ModelRouter>  _log;

    // Keywords that strongly indicate a simple/fast command
    private static readonly HashSet<string> _fastKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // OS actions
        "buka", "open", "tutup", "close", "minimize", "maximize",
        "screenshot", "capture", "layar",
        "volume", "mute", "unmute", "louder", "quieter",
        "shutdown", "restart", "sleep", "lock",
        "copy", "paste", "cut",
        // Time / weather
        "jam", "waktu", "time", "date", "tanggal", "hari",
        "cuaca", "weather",
        // Chitchat
        "halo", "hai", "hi", "hello", "hey",
        "thanks", "terima kasih", "makasih", "thx",
        "oke", "ok", "siap", "noted",
        "bye", "exit", "quit",
        // Simple queries
        "berapa", "siapa", "apa itu", "dimana",
    };

    // Keywords that indicate complex reasoning needed → always route to MAIN
    private static readonly HashSet<string> _mainKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "analisis", "analyze", "analysis",
        "jelaskan", "explain", "explanation",
        "buat", "create", "generate", "tulis", "write",
        "kode", "code", "debug", "error", "fix", "refactor",
        "bandingkan", "compare", "comparison",
        "ringkas", "summarize", "summary",
        "terjemahkan", "translate",
        "rencanakan", "plan", "planning",
        "strategi", "strategy",
        "kenapa", "mengapa", "why", "how",
        "bagaimana", "cara",
    };

    // Keywords that indicate vision context needed
    private static readonly HashSet<string> _visionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "lihat", "look", "see", "gambar", "image", "foto", "photo",
        "screenshot", "layar", "screen",
        "apa yang", "what is", "what's",
        "baca gambar", "read image", "ocr",
    };

    public ModelRouter(IOptions<ZeroConfig> cfg, ILogger<ModelRouter> log)
    {
        _cfg = cfg.Value;
        _log = log;
    }

    public enum Tier { Fast, Main, Vision }

    /// <summary>
    /// Determine which model tier to use for the given input.
    /// Pass hasImages=true when the turn includes image attachments.
    /// </summary>
    public string Route(string input, bool hasImages = false)
    {
        if (!_cfg.EnableModelRouting)
        {
            _log.LogDebug("Routing disabled → using main model.");
            return _cfg.ModelName;
        }

        // Vision tier — image attached OR vision keywords present AND vision model configured
        if (!string.IsNullOrWhiteSpace(_cfg.VisionModelName))
        {
            if (hasImages || ContainsAny(input, _visionKeywords))
            {
                _log.LogDebug("Routing → VISION ({Model})", _cfg.VisionModelName);
                return _cfg.VisionModelName;
            }
        }

        // Main tier — complex keywords always bypass fast model
        if (ContainsAny(input, _mainKeywords))
        {
            _log.LogDebug("Routing → MAIN ({Model})", _cfg.ModelName);
            return _cfg.ModelName;
        }

        // Fast tier — short input OR fast keywords, and fast model is configured
        if (!string.IsNullOrWhiteSpace(_cfg.FastModelName))
        {
            var wordCount = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount <= 8 || ContainsAny(input, _fastKeywords))
            {
                _log.LogDebug("Routing → FAST ({Model})", _cfg.FastModelName);
                return _cfg.FastModelName;
            }
        }

        // Default fallback → main model
        _log.LogDebug("Routing → MAIN fallback ({Model})", _cfg.ModelName);
        return _cfg.ModelName;
    }

    private static bool ContainsAny(string input, HashSet<string> keywords)
    {
        foreach (var kw in keywords)
        {
            if (input.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
