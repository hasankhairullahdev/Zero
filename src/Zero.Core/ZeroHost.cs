using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Zero.Core.LLM;
using Zero.Core.Tray;
using Zero.Core.UI;
using Zero.Core.VoiceEngine;

namespace Zero.Core;

/// <summary>
/// Main ZERO background service.
/// - Text mode:  reads from Console or text-input popup (Ctrl+Shift+Z)
/// - Voice mode: Ctrl+Shift+Space → record mic → Whisper STT → LLM → Kokoro TTS
/// Both modes share the same agentic LLM loop.
/// Ctrl+Shift+X cancels any in-progress response.
/// Tray icon reflects current state (idle/listening/processing).
/// </summary>
public sealed class ZeroHost : BackgroundService
{
    private readonly OllamaClient              _ollama;
    private readonly ToolCallRouter            _router;
    private readonly McpClientManager          _mcp;
    private readonly HotkeyListener            _hotkey;
    private readonly SpeechRecognizer  _stt;
    private readonly KokoroTtsService  _tts;
    private readonly TrayManager       _tray;
    private readonly ILogger<ZeroHost>         _log;
    private readonly ZeroConfig                _cfg;

    // Conversation history shared across text and voice turns
    private readonly List<ChatMessage> _history = [];
    private List<OllamaTool>           _tools   = [];

    // Voice state
    private volatile bool _isRecording;

    // Per-turn cancellation — allows Ctrl+Shift+X to interrupt
    private CancellationTokenSource? _turnCts;
    private readonly object           _turnLock = new();

    // Text input popup — created on TrayManager's STA thread
    private TextInputForm? _textInputForm;

    public ZeroHost(
        OllamaClient              ollama,
        ToolCallRouter            router,
        McpClientManager          mcp,
        HotkeyListener            hotkey,
        SpeechRecognizer  stt,
        KokoroTtsService  tts,
        TrayManager       tray,
        IOptions<ZeroConfig>      cfg,
        ILogger<ZeroHost>         log)
    {
        _ollama = ollama;
        _router = router;
        _mcp    = mcp;
        _hotkey = hotkey;
        _stt    = stt;
        _tts    = tts;
        _tray   = tray;
        _cfg    = cfg.Value;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("ZERO starting up...");

        // Start tray icon (runs its own STA message loop)
        _tray.Start();
        _tray.ExitRequested += (_, _) =>
        {
            _log.LogInformation("Exit requested from tray.");
            _ = StopAsync(ct);
        };
        // Tray "Open text input" button → same as Ctrl+Shift+Z
        _tray.OpenTextInputRequested += (_, _) => ShowTextInputPopup();

        // Initialise MCP servers (wait for Ollama in background, MCP can init independently)
        _ = Task.Run(async () =>
        {
            await WaitForOllamaAsync(ct);
            await _mcp.InitialiseAsync(ct);
            _tools = await _mcp.GetToolsAsync(ct);
            _log.LogInformation("ZERO fully ready. {Count} tools loaded.", _tools.Count);
        }, ct);

        // Seed system prompt
        _history.Add(new ChatMessage { Role = "system", Content = _cfg.SystemPrompt });

        _tray.SetState(TrayManager.TrayState.Idle);
        _log.LogInformation("ZERO ready. Waiting for Ollama + tools in background...");

        Console.WriteLine("\n╔══════════════════════════════════════════════╗");
        Console.WriteLine("║  ZERO is ready.                              ║");
        Console.WriteLine("║  Type a command and press Enter.             ║");
        if (_cfg.EnableStt)
            Console.WriteLine("║  Ctrl+Shift+Space  → voice input             ║");
        Console.WriteLine("║  Ctrl+Shift+Z      → text input popup        ║");
        Console.WriteLine("║  Ctrl+Shift+X      → cancel response         ║");
        Console.WriteLine("╚══════════════════════════════════════════════╝\n");

        // Initialise STT + TTS in background so text input is immediately available
        _ = Task.Run(async () =>
        {
            // STT and TTS init run concurrently to reduce total startup time
            var sttTask = _cfg.EnableStt
                ? Task.Run(async () =>
                {
                    try
                    {
                        await _stt.InitialiseAsync(ct);
                        _hotkey.HotkeyPressed += OnHotkeyPressed;
                        _log.LogInformation("Voice input ready. Press Ctrl+Shift+Space to speak.");
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "STT init failed - voice input disabled."); }
                }, ct)
                : Task.CompletedTask;

            var ttsTask = _cfg.EnableTts
                ? Task.Run(async () => await _tts.InitialiseAsync(ct), ct)
                : Task.CompletedTask;

            await Task.WhenAll(sttTask, ttsTask);

            // Startup greeting — only after TTS is fully ready
            if (_cfg.EnableTts && _cfg.StartupGreeting && _tts.IsReady)
            {
                var hour = DateTime.Now.Hour;
                var greetings = hour switch
                {
                    >= 5  and < 12 => new[]
                    {
                        "Good morning, Hasan. Systems online, coffee optional.",
                        "Morning, Hasan. Ready when you are.",
                        "Rise and shine, Hasan. ZERO is fully operational.",
                    },
                    >= 12 and < 17 => new[]
                    {
                        "Good afternoon, Hasan. What are we breaking today?",
                        "Afternoon, Hasan. All systems go.",
                        "Hey Hasan, ZERO reporting for duty.",
                    },
                    >= 17 and < 21 => new[]
                    {
                        "Good evening, Hasan. Long day? I got you.",
                        "Evening, Hasan. ZERO is online and ready.",
                        "Good evening. What do you need, Hasan?",
                    },
                    _ => new[]
                    {
                        "Still up, Hasan? ZERO is here.",
                        "Late night session? I'm with you, Hasan.",
                        "Good night, Hasan. Or good morning. Hard to tell at this point.",
                    }
                };
                var greeting = greetings[new Random().Next(greetings.Length)];
                Console.WriteLine($"\nZERO: {greeting}\n");
                try { await _tts.SpeakAsync(greeting, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "Startup greeting TTS failed."); }
            }
        }, ct);

        // Wire Ctrl+Shift+Z and Ctrl+Shift+X
        _hotkey.TextInputRequested += (_, _) => ShowTextInputPopup();
        _hotkey.CancelRequested    += (_, _) => CancelCurrentTurn();

        await TextInputLoopAsync(ct);
    }

    // ── Text input loop ───────────────────────────────────────────────────────

    private async Task TextInputLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Console.Write("You: ");
            string? input;
            try
            {
                input = await Task.Run(Console.ReadLine, ct);
            }
            catch (OperationCanceledException) { break; }

            if (string.IsNullOrWhiteSpace(input))
                continue;

            await ProcessInputAsync(input, ct);
        }
    }

    // ── Text input popup (Ctrl+Shift+Z) ──────────────────────────────────────

    private void ShowTextInputPopup()
    {
        // The form must run on the tray's STA thread — post to it
        _tray.PostToStaThread(() =>
        {
            _textInputForm ??= new TextInputForm();
            _textInputForm.CommandSubmitted -= OnPopupCommand;
            _textInputForm.CommandSubmitted += OnPopupCommand;
            _textInputForm.ShowAndFocus();
        });
    }

    private void OnPopupCommand(object? sender, string text)
    {
        Console.WriteLine($"You (popup): {text}");
        _ = Task.Run(async () => await ProcessInputAsync(text, CancellationToken.None));
    }

    // ── Cancel active turn (Ctrl+Shift+X) ────────────────────────────────────

    private void CancelCurrentTurn()
    {
        lock (_turnLock)
        {
            if (_turnCts is null) return;
            _log.LogInformation("Turn cancelled by user.");
            _tts.CancelSpeech();
            _turnCts.Cancel();
        }
        Console.WriteLine("\n[ZERO] Response cancelled.\n");
    }

    // ── Hotkey handler (voice push-to-talk toggle) ────────────────────────────

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            if (!_isRecording)
            {
                _isRecording = true;
                _tray.SetState(TrayManager.TrayState.Listening);
                Console.WriteLine("\n[ZERO] 🎙  Listening... (press hotkey again to send)");
                await _stt.StartRecordingAsync();
            }
            else
            {
                _isRecording = false;
                _tray.SetState(TrayManager.TrayState.Processing);
                Console.WriteLine("[ZERO] 🛑  Processing speech...");

                string text;
                try
                {
                    text = await _stt.StopRecordingAsync();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "STT failed.");
                    _tray.SetState(TrayManager.TrayState.Idle);
                    Console.WriteLine("[ZERO] STT error — please try again.\n");
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine("[ZERO] (nothing heard)\n");
                    _tray.SetState(TrayManager.TrayState.Idle);
                    return;
                }

                Console.WriteLine($"You (voice): {text}");
                await ProcessInputAsync(text, CancellationToken.None);
            }
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Split text into speakable sentences to reduce TTS latency.</summary>
    private async Task WaitForOllamaAsync(CancellationToken ct)
    {
        const int maxAttempts  = 30;   // 30 x 2s = up to 60 seconds
        const int delayMs      = 2000;
        var url = $"{_cfg.OllamaBaseUrl}/api/tags";

        for (int i = 0; i < maxAttempts; i++)
        {
            try
            {
                var ready = await _ollama.PingAsync(ct);
                if (ready) { _log.LogInformation("Ollama is ready."); return; }
            }
            catch { /* not ready yet */ }

            if (i == 0)
                _log.LogInformation("Waiting for Ollama to start...");

            _tray.SetState(TrayManager.TrayState.Processing);
            await Task.Delay(delayMs, ct);
        }

        _log.LogWarning("Ollama did not respond after {S}s — continuing anyway.", maxAttempts * delayMs / 1000);
    }

    private static IEnumerable<string> SplitSentences(string text)
    {
        var parts = System.Text.RegularExpressions.Regex
            .Split(text.Trim(), @"(?<=[.!?])\s*")
            .Select(s => s.Trim())
            .Where(s => s.Length >= 3);

        foreach (var part in parts)
            yield return part;
    }

    // ── Core agentic loop (shared by text, voice, and popup) ─────────────────

    public async Task ProcessInputAsync(string userInput, CancellationToken hostCt)
    {
        // Create a linked CTS so both host shutdown AND Ctrl+Shift+X can cancel
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        lock (_turnLock) { _turnCts = turnCts; }

        var ct = turnCts.Token;

        // If tools not loaded yet, Ollama is still starting up — warn user
        if (_tools.Count == 0)
        {
            const string notReady = "Hold on, I'm still connecting to my brain. Give me a few seconds.";
            Console.WriteLine($"\nZERO: {notReady}\n");
            if (_cfg.EnableTts && _tts.IsReady)
                await _tts.SpeakAsync(notReady, ct);
            return;
        }

        _tray.SetState(TrayManager.TrayState.Processing);
        _history.Add(new ChatMessage { Role = "user", Content = userInput });

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ChatMessage? assistantMsg;
                try
                {
                    assistantMsg = await _ollama.ChatAsync(_history, _tools, _cfg.EnableThinking, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Ollama request failed.");
                    Console.WriteLine($"\n[ZERO] Error communicating with Ollama: {ex.Message}\n");
                    break;
                }

                if (assistantMsg is null)
                {
                    _log.LogWarning("Received null response from Ollama.");
                    break;
                }

                _history.Add(assistantMsg);

                // Tool calls → execute and loop back
                if (assistantMsg.ToolCalls is { Count: > 0 })
                {
                    List<ChatMessage> toolResults;
                    try
                    {
                        toolResults = await _router.ExecuteAsync(assistantMsg, ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Tool execution failed.");
                        Console.WriteLine($"\n[ZERO] Tool error: {ex.Message}\n");
                        break;
                    }
                    _history.AddRange(toolResults);
                    continue;
                }

                // Final text reply
                var reply = assistantMsg.Content ?? "(no response)";
                Console.WriteLine($"\nZERO: {reply}\n");

                if (_cfg.EnableTts)
                {
                    foreach (var sentence in SplitSentences(reply))
                    {
                        if (ct.IsCancellationRequested) break;
                        try   { await _tts.SpeakAsync(sentence, ct); }
                        catch (OperationCanceledException) { break; }
                        catch (Exception ex) { _log.LogWarning(ex, "TTS failed."); break; }
                    }
                }

                break;
            }
        }
        finally
        {
            lock (_turnLock) { _turnCts = null; }
            _tray.SetState(TrayManager.TrayState.Idle);
        }
    }
}
