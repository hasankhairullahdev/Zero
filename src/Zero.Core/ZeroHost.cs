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
/// - Wake word:  "Hey Jarvis" → record via VAD → Whisper STT → LLM → Kokoro TTS
/// Both modes share the same agentic LLM loop.
/// Ctrl+Shift+X cancels any in-progress response.
/// Tray icon reflects current state (idle/listening/processing) with animation.
/// Memory persists conversation history across sessions.
/// Daily briefing spoken once per morning.
/// </summary>
public sealed class ZeroHost : BackgroundService
{
    private readonly OllamaClient              _ollama;
    private readonly ToolCallRouter            _router;
    private readonly McpClientManager          _mcp;
    private readonly HotkeyListener            _hotkey;
    private readonly SpeechRecognizer          _stt;
    private readonly KokoroTtsService          _tts;
    private readonly TrayManager               _tray;
    private readonly WakeWordListener          _wakeWord;
    private readonly SessionMemory             _memory;
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
        SpeechRecognizer          stt,
        KokoroTtsService          tts,
        TrayManager               tray,
        WakeWordListener          wakeWord,
        SessionMemory             memory,
        IOptions<ZeroConfig>      cfg,
        ILogger<ZeroHost>         log)
    {
        _ollama   = ollama;
        _router   = router;
        _mcp      = mcp;
        _hotkey   = hotkey;
        _stt      = stt;
        _tts      = tts;
        _tray     = tray;
        _wakeWord = wakeWord;
        _memory   = memory;
        _cfg      = cfg.Value;
        _log      = log;
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
        _tray.OpenTextInputRequested += (_, _) => ShowTextInputPopup();

        // Initialise MCP servers in background
        _ = Task.Run(async () =>
        {
            await WaitForOllamaAsync(ct);
            await _mcp.InitialiseAsync(ct);
            _tools = await _mcp.GetToolsAsync(ct);
            _log.LogInformation("ZERO fully ready. {Count} tools loaded.", _tools.Count);
        }, ct);

        // Seed system prompt
        _history.Add(new ChatMessage { Role = "system", Content = _cfg.SystemPrompt });

        // Load persisted memory AFTER system prompt
        if (_cfg.EnableMemory)
            _memory.Load(_history);

        _tray.SetState(TrayManager.TrayState.Idle);
        _log.LogInformation("ZERO ready. Waiting for Ollama + tools in background...");

        Console.WriteLine("\n╔══════════════════════════════════════════════╗");
        Console.WriteLine("║  ZERO is ready.                              ║");
        Console.WriteLine("║  Type a command and press Enter.             ║");
        if (_cfg.EnableStt)
            Console.WriteLine("║  Ctrl+Shift+Space  → voice input             ║");
        if (_cfg.EnableWakeWord)
            Console.WriteLine("║  Say 'Hey Jarvis'  → wake word               ║");
        Console.WriteLine("║  Ctrl+Shift+Z      → text input popup        ║");
        Console.WriteLine("║  Ctrl+Shift+X      → cancel response         ║");
        Console.WriteLine("╚══════════════════════════════════════════════╝\n");

        // Wire hotkeys
        _hotkey.TextInputRequested += (_, _) => ShowTextInputPopup();
        _hotkey.CancelRequested    += (_, _) => CancelCurrentTurn();

        // Initialise STT + TTS + wake word in background
        _ = Task.Run(async () =>
        {
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

            // Wake word listener (depends on STT being ready for shared Whisper)
            if (_cfg.EnableWakeWord)
            {
                try
                {
                    await _wakeWord.InitialiseAsync(ct);
                    _wakeWord.WakeWordDetected += OnWakeWordDetected;
                    await _wakeWord.StartAsync(ct);
                }
                catch (Exception ex) { _log.LogWarning(ex, "Wake word init failed."); }
            }

            // Daily briefing — only after TTS ready
            if (_cfg.EnableDailyBriefing && _cfg.EnableTts && _tts.IsReady)
            {
                var briefing = DailyBriefing.TryGenerate(_log);
                if (briefing is not null)
                {
                    Console.WriteLine($"\nZERO (briefing): {briefing}\n");
                    try { await _tts.SpeakAsync(briefing, ct); }
                    catch (Exception ex) { _log.LogWarning(ex, "Daily briefing TTS failed."); }
                    return; // skip regular greeting if briefing was delivered
                }
            }

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
                _wakeWord.Pause();
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
                    _wakeWord.Resume();
                    Console.WriteLine("[ZERO] STT error — please try again.\n");
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine("[ZERO] (nothing heard)\n");
                    _tray.SetState(TrayManager.TrayState.Idle);
                    _wakeWord.Resume();
                    return;
                }

                Console.WriteLine($"You (voice): {text}");
                await ProcessInputAsync(text, CancellationToken.None);
                _wakeWord.Resume();
            }
        });
    }

    // ── Wake word handler ─────────────────────────────────────────────────────

    private void OnWakeWordDetected(object? sender, byte[] wavBytes)
    {
        _ = Task.Run(async () =>
        {
            // Pause wake word while we handle this turn (mic already stopped in WakeWordListener)
            _wakeWord.Pause();
            _tray.SetState(TrayManager.TrayState.Listening);
            Console.WriteLine("\n[ZERO] 🎙  Wake word detected — transcribing...");

            string text;
            try
            {
                text = await _stt.TranscribeWavBytesAsync(wavBytes);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Wake word STT failed.");
                _tray.SetState(TrayManager.TrayState.Idle);
                _wakeWord.Resume();
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                // Wake word terdeteksi tapi tidak ada command — resume saja
                Console.WriteLine("[ZERO] (wake word detected, no command heard)\n");
                _tray.SetState(TrayManager.TrayState.Idle);
                _wakeWord.Resume();
                return;
            }

            Console.WriteLine($"You (wake): {text}");
            await ProcessInputAsync(text, CancellationToken.None);
            _wakeWord.Resume();
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task WaitForOllamaAsync(CancellationToken ct)
    {
        const int maxAttempts = 30;
        const int delayMs     = 2000;

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

    // ── Core agentic loop (shared by text, voice, popup, and wake word) ───────

    public async Task ProcessInputAsync(string userInput, CancellationToken hostCt)
    {
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        lock (_turnLock) { _turnCts = turnCts; }

        var ct = turnCts.Token;

        // Wait up to 10s for tools to load (MCP init may still be in progress)
        if (_tools.Count == 0)
        {
            _log.LogInformation("Waiting for tools to load...");
            for (int i = 0; i < 20 && _tools.Count == 0; i++)
                await Task.Delay(500, ct);
        }

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

        string? finalReply = null;

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
                finalReply = assistantMsg.Content ?? "(no response)";
                Console.WriteLine($"\nZERO: {finalReply}\n");

                if (_cfg.EnableTts)
                {
                    foreach (var sentence in SplitSentences(finalReply))
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

            // Persist short-term memory after every turn + trim live context
            if (_cfg.EnableMemory)
                _memory.Save(_history);

            // Tray balloon notification with reply snippet
            if (finalReply is not null && _cfg.EnableReplyNotification && _cfg.NotificationMaxChars > 0)
            {
                var snippet = finalReply.Length > _cfg.NotificationMaxChars
                    ? finalReply[.._cfg.NotificationMaxChars] + "…"
                    : finalReply;
                _tray.ShowNotification("ZERO", snippet);
            }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        // Update long-term summary when ZERO shuts down
        if (_cfg.EnableMemory)
            _memory.UpdateLongTermSummary(_history);

        await base.StopAsync(ct);
    }
}
