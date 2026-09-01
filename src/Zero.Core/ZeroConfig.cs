namespace Zero.Core;

/// <summary>Configuration model bound from zero.config.json.</summary>
public sealed class ZeroConfig
{
    /// <summary>Ollama base URL (default: http://127.0.0.1:11434)</summary>
    public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>LLM model name (e.g. qwen3:32b)</summary>
    public string ModelName { get; set; } = "qwen3:32b";

    /// <summary>System prompt sent at the start of every conversation.</summary>
    public string SystemPrompt { get; set; } =
        "You are ZERO, a helpful AI assistant running locally on Windows. " +
        "You can control the operating system using tools. " +
        "Always respond in the same language the user uses (Indonesian or English). " +
        "When using tools, execute them and report results concisely.";

    /// <summary>Hotkey to activate voice input (virtual key code, default: Space = 0x20).</summary>
    public int HotkeyVk { get; set; } = 0x20; // VK_SPACE

    /// <summary>Hotkey modifiers (MOD_CTRL=2, MOD_SHIFT=4 → default: Ctrl+Shift+Space = 6).</summary>
    public int HotkeyMod { get; set; } = 6;

    /// <summary>Path to Zero.FileManager project (used to spawn MCP server).</summary>
    public string FileManagerProjectPath { get; set; } = "src/Zero.FileManager/Zero.FileManager.csproj";

    /// <summary>Path to Zero.SystemControl project (used to spawn MCP server).</summary>
    public string SystemControlProjectPath { get; set; } = "src/Zero.SystemControl/Zero.SystemControl.csproj";

    /// <summary>Path to Zero.WebAccess project (used to spawn MCP server).</summary>
    public string WebAccessProjectPath { get; set; } = "src/Zero.WebAccess/Zero.WebAccess.csproj";

    /// <summary>Whether to enable qwen3 thinking mode for complex queries.</summary>
    public bool EnableThinking { get; set; } = false;

    // ── Voice ─────────────────────────────────────────────────────────────────

    /// <summary>Whisper model size (tiny/base/small/medium/large). Default: medium.</summary>
    public string WhisperModel { get; set; } = "Medium";

    /// <summary>Whisper language hint. "auto" for auto-detect, or "id", "en", etc.</summary>
    public string WhisperLanguage { get; set; } = "auto";

    /// <summary>TTS speech rate (-10 to 10, 0 = normal).</summary>
    public int TtsRate { get; set; } = 0;

    /// <summary>TTS volume (0–100).</summary>
    public int TtsVolume { get; set; } = 100;

    /// <summary>Enable voice output (TTS). If false, ZERO only prints to console.</summary>
    public bool EnableTts { get; set; } = true;

    /// <summary>Enable voice input (STT). If false, ZERO only accepts text input.</summary>
    public bool EnableStt { get; set; } = true;

    /// <summary>Kokoro TTS voice name (e.g. "af_heart", "am_adam"). See voices/ folder for options.</summary>
    public string KokoroVoice { get; set; } = "af_heart";

    /// <summary>Speak a greeting when ZERO starts up (time-aware: morning/afternoon/evening/night).</summary>
    public bool StartupGreeting { get; set; } = true;

    /// <summary>
    /// Limit Ollama GPU layers to free up VRAM for Whisper CUDA.
    /// null = let Ollama decide (default). Set e.g. 20 to offload some layers to CPU.
    /// </summary>
    public int? OllamaNumGpu { get; set; } = null;
}
