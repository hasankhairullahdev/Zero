using KokoroSharp;
using KokoroSharp.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zero.Core.VoiceEngine;

/// <summary>
/// Kokoro TTS service — high-quality neural TTS via KokoroSharp.
/// Model (~320MB) is auto-downloaded on first use.
/// </summary>
public sealed class KokoroTtsService : IDisposable
{
    private readonly ILogger<KokoroTtsService> _log;
    private readonly ZeroConfig               _cfg;

    private KokoroTTS?   _tts;
    private KokoroVoice? _voice;
    private bool         _ready;

    public bool IsReady => _ready;

    public KokoroTtsService(IOptions<ZeroConfig> cfg, ILogger<KokoroTtsService> log)
    {
        _cfg = cfg.Value;
        _log = log;
    }

    /// <summary>Load model and voice. Safe to call multiple times.</summary>
    public Task InitialiseAsync(CancellationToken ct = default)
    {
        if (_ready) return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                _log.LogInformation("Loading Kokoro TTS model (voice={Voice})...", _cfg.KokoroVoice);

                // Pass explicit path so KokoroSharp never tries to download to system32
                var modelPath = Path.Combine(AppContext.BaseDirectory, "kokoro.onnx");
                _tts   = File.Exists(modelPath)
                    ? KokoroTTS.LoadModel(modelPath)
                    : KokoroTTS.LoadModel();   // fallback: auto-download (requires write access)
                _voice = KokoroVoiceManager.GetVoice(_cfg.KokoroVoice);
                _ready = true;
                _log.LogInformation("Kokoro TTS ready (voice={Voice}).", _cfg.KokoroVoice);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Kokoro TTS failed to initialise — TTS will be disabled.");
            }
        }, ct);
    }

    /// <summary>Speak text and wait until playback completes.</summary>
    public Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (!_ready || _tts is null || _voice is null || string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        // Strip emoji and invalid Unicode that Kokoro/MisakiSharp cannot phonemize
        text = StripUnspeakable(text);
        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        return Task.Run(() =>
        {
            try
            {
                // SpeakFast = lower latency, plays audio as it's generated
                var handle = _tts.SpeakFast(text, _voice);

                // Wait for job to start (give Kokoro time to enqueue audio)
                Thread.Sleep(100);

                // Poll until job done AND all playback handles finished
                while (true)
                {
                    if (ct.IsCancellationRequested) { _tts.StopPlayback(); return; }

                    var allDone = handle.Job.isDone
                        && (handle.ReadyPlaybackHandles.Count == 0
                            || handle.ReadyPlaybackHandles.All(h =>
                                h.State == KokoroSharp.Core.KokoroPlaybackHandleState.Completed
                                || h.Aborted));

                    if (allDone) break;
                    Thread.Sleep(50);
                }

                // Extra buffer so last audio chunk finishes playing
                Thread.Sleep(200);
            }
            catch (OperationCanceledException) { _tts?.StopPlayback(); }
            catch (Exception ex) { _log.LogWarning(ex, "Kokoro TTS playback error."); }
        }, ct);
    }

    /// <summary>Remove emoji, file paths, long numbers and other characters Kokoro cannot phonemize.</summary>
    private static string StripUnspeakable(string text)
    {
        System.Func<string, string, string, string> rx = System.Text.RegularExpressions.Regex.Replace;

        // Remove file paths (e.g. C:\Users\...\file.png or /home/user/file)
        text = rx(text, @"[A-Za-z]:\\[^\s`'""]+", " ");
        text = rx(text, @"/[^\s`'""]{4,}", " ");

        // Remove inline code blocks / backtick spans
        text = rx(text, @"`[^`]*`", " ");

        // Replace long number sequences (timestamps, IDs) with nothing
        text = rx(text, @"\b\d{5,}\b", " ");

        // Strip markdown formatting
        text = rx(text, @"[*_#~>|]", " ");

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.EnumerateRunes())
        {
            if (c.Value <= 0x024F
                || c.Value == 0x2019
                || c.Value == 0x201C
                || c.Value == 0x201D
                || c.Value == 0x2014
                || c.Value == 0x2013)
            {
                sb.Append(c.ToString());
            }
            else
            {
                sb.Append(' ');
            }
        }

        return rx(sb.ToString(), @" {2,}", " ").Trim();
    }

    /// <summary>Immediately stop any in-progress speech.</summary>
    public void CancelSpeech() => _tts?.StopPlayback();

    public void Dispose() => _tts?.Dispose();
}
