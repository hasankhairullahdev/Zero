using System.Speech.Synthesis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Zero.Core.VoiceEngine;

/// <summary>
/// Text-to-speech wrapper using Windows SAPI (System.Speech).
/// Phase 2 baseline — will be replaced by Kokoro TTS in Phase 3.
/// </summary>
public sealed class SpeechSynthesizerService : IDisposable
{
    private readonly SpeechSynthesizer           _synth;
    private readonly ILogger<SpeechSynthesizerService> _log;
    private readonly ZeroConfig                  _cfg;

    public SpeechSynthesizerService(IOptions<ZeroConfig> cfg, ILogger<SpeechSynthesizerService> log)
    {
        _cfg   = cfg.Value;
        _log   = log;
        _synth = new SpeechSynthesizer();
        _synth.SetOutputToDefaultAudioDevice();

        // Apply config (rate/volume)
        _synth.Rate   = _cfg.TtsRate;
        _synth.Volume = _cfg.TtsVolume;

        LogAvailableVoices();
    }

    /// <summary>Speak text asynchronously. Returns when speech is complete.</summary>
    public Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        _log.LogDebug("TTS speaking: {Text}", text);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCompleted(object? s, SpeakCompletedEventArgs e)
        {
            _synth.SpeakCompleted -= OnCompleted;
            if (e.Cancelled || ct.IsCancellationRequested)
                tcs.TrySetCanceled(ct);
            else if (e.Error is not null)
                tcs.TrySetException(e.Error);
            else
                tcs.TrySetResult();
        }

        ct.Register(() =>
        {
            _synth.SpeakAsyncCancelAll();
            tcs.TrySetCanceled(ct);
        });

        _synth.SpeakCompleted += OnCompleted;
        _synth.SpeakAsync(text);

        return tcs.Task;
    }

    /// <summary>Cancel any in-progress speech immediately.</summary>
    public void CancelSpeech() => _synth.SpeakAsyncCancelAll();

    private void LogAvailableVoices()
    {
        foreach (var voice in _synth.GetInstalledVoices())
            _log.LogDebug("TTS voice available: {Name} ({Culture})",
                voice.VoiceInfo.Name, voice.VoiceInfo.Culture);
    }

    public void Dispose() => _synth.Dispose();
}
