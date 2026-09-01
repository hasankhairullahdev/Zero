using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace Zero.Core.VoiceEngine;

/// <summary>
/// Records audio from the default microphone and transcribes it using
/// Whisper.net with CUDA acceleration. The Whisper model is downloaded
/// automatically on first run and cached locally.
/// </summary>
public sealed class SpeechRecognizer : IAsyncDisposable
{
    private readonly ZeroConfig              _cfg;
    private readonly ILogger<SpeechRecognizer> _log;

    private WhisperFactory?        _factory;
    private WhisperProcessorBuilder? _processorBuilder;
    private WhisperProcessor?      _processor;
    private bool                   _initialised;

    // Audio recording state
    private WaveInEvent?      _waveIn;
    private MemoryStream?     _recordingBuffer;
    private WaveFileWriter?   _waveWriter;
    private TaskCompletionSource<byte[]>? _recordingTcs;

    private static readonly string ModelCacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ZERO", "models");

    public SpeechRecognizer(IOptions<ZeroConfig> cfg, ILogger<SpeechRecognizer> log)
    {
        _cfg = cfg.Value;
        _log = log;
    }

    /// <summary>Download model if needed and initialise Whisper processor.</summary>
    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        if (_initialised) return;

        Directory.CreateDirectory(ModelCacheDir);
        var modelPath = Path.Combine(ModelCacheDir, $"ggml-{_cfg.WhisperModel}.bin");

        if (!File.Exists(modelPath))
        {
            _log.LogInformation("Whisper model '{Model}' not found — downloading...", _cfg.WhisperModel);
            var modelType = Enum.Parse<GgmlType>(_cfg.WhisperModel, ignoreCase: true);
            await using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(modelType, cancellationToken: ct);
            await using var fileStream  = File.OpenWrite(modelPath);
            await modelStream.CopyToAsync(fileStream, ct);
            _log.LogInformation("Whisper model downloaded to {Path}", modelPath);
        }

        // Force CUDA to be tried first before falling back to CPU
        RuntimeOptions.RuntimeLibraryOrder = [
            RuntimeLibrary.Cuda,
            RuntimeLibrary.Cpu,
        ];

        // Verify CUDA native lib is present beside the exe
        var cudaDll = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "ggml-cuda-whisper.dll");
        _log.LogInformation("CUDA dll present: {Present} at {Path}", File.Exists(cudaDll), cudaDll);

        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true, GpuDevice = 0 });
        _log.LogInformation("Whisper factory created. Preferred runtime: {Runtime}", RuntimeOptions.RuntimeLibraryOrder[0]);

        // Processor is created fresh per-transcription to avoid internal state accumulation
        // _processorBuilder is kept so we can BuildNew() each time without re-loading the model
        _processorBuilder = _factory.CreateBuilder()
                                    .WithLanguage(_cfg.WhisperLanguage)
                                    .WithNoContext();          // no cross-segment context carry-over
        _processor = _processorBuilder.Build();

        _initialised = true;
        _log.LogInformation("Whisper STT ready (model={Model}, lang={Lang}).",
            _cfg.WhisperModel, _cfg.WhisperLanguage);
    }

    /// <summary>
    /// Record audio until <see cref="StopRecordingAsync"/> is called,
    /// then transcribe and return the text.
    /// </summary>
    public Task StartRecordingAsync()
    {
        if (_recordingTcs is not null)
            return Task.CompletedTask; // already recording

        _recordingBuffer = new MemoryStream();
        var waveFormat   = new WaveFormat(16000, 16, 1); // Whisper expects 16kHz mono
        _waveWriter      = new WaveFileWriter(_recordingBuffer, waveFormat);
        _recordingTcs    = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        _waveIn = new WaveInEvent
        {
            WaveFormat      = waveFormat,
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable    += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();

        _log.LogDebug("Recording started.");
        return Task.CompletedTask;
    }

    /// <summary>Stop recording and return the transcribed text.</summary>
    public async Task<string> StopRecordingAsync(CancellationToken ct = default)
    {
        if (_waveIn is null || _recordingTcs is null)
            return string.Empty;

        _waveIn.StopRecording();

        var t0 = DateTime.Now;
        var wavBytes = await _recordingTcs.Task.WaitAsync(ct);
        _log.LogInformation("Audio flush: {Ms}ms, size: {Bytes} bytes",
            (int)(DateTime.Now - t0).TotalMilliseconds, wavBytes.Length);

        // Transcribe
        var t1  = DateTime.Now;
        var text = await TranscribeAsync(wavBytes, ct);
        _log.LogInformation("Transcribe: {Ms}ms → '{Text}'",
            (int)(DateTime.Now - t1).TotalMilliseconds, text);

        CleanupRecording();
        return text;
    }

    private async Task<string> TranscribeAsync(byte[] wavBytes, CancellationToken ct)
    {
        if (_processorBuilder is null)
            throw new InvalidOperationException("SpeechRecognizer not initialised.");

        // Trim trailing silence so Whisper doesn't pad unnecessarily to 30s
        var trimmedBytes = TrimTrailingSilence(wavBytes);
        _log.LogInformation("Audio trimmed: {Before} -> {After} bytes",
            wavBytes.Length, trimmedBytes.Length);

        // Fresh processor per call — avoids internal KV-cache state accumulation
        await using var processor = _processorBuilder.Build();

        using var ms = new MemoryStream(trimmedBytes);
        var sb       = new System.Text.StringBuilder();

        await foreach (var segment in processor.ProcessAsync(ms, ct))
            sb.Append(segment.Text);

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Trim trailing silence from a 16kHz 16-bit mono WAV byte array.
    /// Uses NAudio WaveFileWriter to produce a valid WAV output.
    /// </summary>
    private static byte[] TrimTrailingSilence(byte[] wavBytes, double silenceThreshold = 0.01, int keepPaddingMs = 300)
    {
        const int sampleRate     = 16000;
        const int bytesPerSample = 2; // 16-bit mono

        using var inputMs  = new MemoryStream(wavBytes);
        using var reader   = new NAudio.Wave.WaveFileReader(inputMs);

        var fmt            = reader.WaveFormat;
        var totalSamples   = (int)(reader.SampleCount);
        var allPcm         = new short[totalSamples];
        var buf            = new byte[bytesPerSample];
        for (int i = 0; i < totalSamples; i++)
        {
            if (reader.Read(buf, 0, bytesPerSample) < bytesPerSample) break;
            allPcm[i] = BitConverter.ToInt16(buf, 0);
        }

        // Find last non-silent sample
        var threshold      = (short)(short.MaxValue * silenceThreshold);
        var lastLoud       = totalSamples - 1;
        for (int i = totalSamples - 1; i >= 0; i--)
        {
            if (Math.Abs(allPcm[i]) > threshold) { lastLoud = i; break; }
        }

        // Keep audio up to lastLoud + padding
        var paddingSamples = (sampleRate * keepPaddingMs) / 1000;
        var keepSamples    = Math.Min(lastLoud + paddingSamples, totalSamples);

        // Write trimmed PCM back into a valid WAV via NAudio
        using var outputMs = new MemoryStream();
        using (var writer  = new NAudio.Wave.WaveFileWriter(outputMs, fmt))
        {
            var pcmBytes = new byte[keepSamples * bytesPerSample];
            for (int i = 0; i < keepSamples; i++)
                BitConverter.TryWriteBytes(pcmBytes.AsSpan(i * bytesPerSample), allPcm[i]);
            writer.Write(pcmBytes, 0, pcmBytes.Length);
        }

        return outputMs.ToArray();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _waveWriter?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _waveWriter?.Flush();
        var wavBytes = _recordingBuffer?.ToArray() ?? [];
        _recordingTcs?.TrySetResult(wavBytes);
        _log.LogDebug("Recording stopped. Bytes captured: {N}", wavBytes.Length);
    }

    private void CleanupRecording()
    {
        _waveIn?.Dispose();
        _waveWriter?.Dispose();
        _recordingBuffer?.Dispose();
        _waveIn          = null;
        _waveWriter      = null;
        _recordingBuffer = null;
        _recordingTcs    = null;
    }

    public async ValueTask DisposeAsync()
    {
        CleanupRecording();
        if (_processor is not null) await _processor.DisposeAsync();
        _factory?.Dispose();
        _processorBuilder = null;
    }
}
