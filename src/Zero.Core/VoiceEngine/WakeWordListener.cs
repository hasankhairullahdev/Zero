using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;

namespace Zero.Core.VoiceEngine;

/// <summary>
/// Always-on wake word listener using OpenWakeWord ONNX model (hey_jarvis).
/// Architecture:
///   MIC (NAudio, 16kHz, continuous)
///    → 80ms audio frames fed into a ring buffer
///    → OpenWakeWord ONNX scores each frame (~1ms/frame, CPU)
///    → score > threshold → capture post-wake audio via VAD
///    → return captured PCM bytes to ZeroHost for Whisper STT
///
/// Audio is NEVER written to disk. Only a ~4s ring buffer lives in RAM.
/// Model is auto-downloaded from GitHub releases on first run.
/// </summary>
public sealed class WakeWordListener : IAsyncDisposable
{
    // ── Public API ─────────────────────────────────────────────────────────────
    /// <summary>Fired on thread pool with the captured post-wake PCM WAV bytes.</summary>
    public event EventHandler<byte[]>? WakeWordDetected;

    // ── Constants ──────────────────────────────────────────────────────────────
    private static readonly WaveFormat WavFmt = new(16000, 16, 1); // 16kHz, 16-bit, mono

    private const int FrameMs         = 80;    // OWW processes 80ms frames
    private const int FrameSamples    = 16000 * FrameMs / 1000;   // 1280 samples
    private const int FrameBytes      = FrameSamples * 2;          // 2560 bytes (16-bit)
    private const int RingSeconds     = 3;
    private const int RingBytes       = 16000 * 2 * RingSeconds;   // ~96 KB
    private const int CaptureMaxMs    = 6000;  // max post-wake capture window
    private const int SilenceMs       = 1200;  // stop capture after this silence
    private const double WakeThreshold = 0.5;  // OWW confidence threshold
    private const double SilenceEnergy = 0.005; // RMS energy below = silence

    private static readonly string ModelDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ZERO", "models");

    private const string ModelFileName = "hey_jarvis_v0.1.onnx";
    private const string ModelUrl =
        "https://github.com/dscripka/openWakeWord/releases/download/v0.6.0/hey_jarvis_v0.1.onnx";

    // ── State ──────────────────────────────────────────────────────────────────
    private readonly ZeroConfig                 _cfg;
    private readonly ILogger<WakeWordListener>  _log;

    private InferenceSession? _session;

    private WaveInEvent?       _waveIn;
    private readonly byte[]    _ring    = new byte[RingBytes];
    private int                _ringPos;                 // write position in ring buffer
    private readonly byte[]    _frameAccum = new byte[FrameBytes]; // accumulate until full frame
    private int                _framePos;

    private volatile bool      _paused;
    private volatile bool      _capturing;   // true while recording post-wake audio
    private MemoryStream?      _captureStream;
    private WaveFileWriter?    _captureWriter;
    private int                _silenceSamples;
    private int                _capturedSamples;

    private CancellationTokenSource? _cts;

    // ── OWW internal melspectrogram state ─────────────────────────────────────
    // OWW expects a rolling 76-frame (80ms each = ~6s) mel feature history.
    // We maintain it as a float[1, 76, 32] tensor updated every frame.
    private const int MelFrames   = 76;
    private const int MelFeatures = 32;
    private readonly float[,,] _melHistory = new float[1, MelFrames, MelFeatures];
    private InferenceSession?  _melSession;  // melspectrogram feature extractor

    private const string MelModelFileName = "melspectrogram.onnx";
    private const string MelModelUrl =
        "https://github.com/dscripka/openWakeWord/releases/download/v0.6.0/melspectrogram.onnx";

    public WakeWordListener(IOptions<ZeroConfig> cfg, ILogger<WakeWordListener> log)
    {
        _cfg = cfg.Value;
        _log = log;
    }

    // ── Initialise ─────────────────────────────────────────────────────────────

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelDir);

        await EnsureModelAsync(MelModelFileName, MelModelUrl, ct);
        await EnsureModelAsync(ModelFileName,    ModelUrl,    ct);

        var opts = new SessionOptions();
        opts.AppendExecutionProvider_CPU();

        _melSession = new InferenceSession(Path.Combine(ModelDir, MelModelFileName), opts);
        _session    = new InferenceSession(Path.Combine(ModelDir, ModelFileName),    opts);

        _log.LogInformation("OpenWakeWord ready (model={Model}, threshold={T})",
            ModelFileName, WakeThreshold);
    }

    private async Task EnsureModelAsync(string fileName, string url, CancellationToken ct)
    {
        var path = Path.Combine(ModelDir, fileName);
        if (File.Exists(path)) return;

        _log.LogInformation("Downloading wake word model: {File}...", fileName);
        using var http   = new HttpClient();
        await using var resp = await http.GetStreamAsync(url, ct);
        await using var fs   = File.OpenWrite(path);
        await resp.CopyToAsync(fs, ct);
        _log.LogInformation("Wake word model downloaded: {File}", fileName);
    }

    // ── Start / Stop ───────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        if (!_cfg.EnableWakeWord || _session is null) return Task.CompletedTask;

        _cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _waveIn = new WaveInEvent { WaveFormat = WavFmt, BufferMilliseconds = FrameMs };
        _waveIn.DataAvailable    += OnData;
        _waveIn.StartRecording();

        _log.LogInformation("Wake word listener started — say 'Hey Jarvis' to activate.");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        return Task.CompletedTask;
    }

    /// <summary>Pause wake word detection while main pipeline is active (avoids mic contention).</summary>
    public void Pause()  => _paused = true;

    /// <summary>Resume detection after main pipeline finishes.</summary>
    public void Resume()
    {
        _paused    = false;
        _capturing = false;
        _captureStream?.Dispose();
        _captureWriter?.Dispose();
        _captureStream  = null;
        _captureWriter  = null;
    }

    // ── Audio data handler (called on NAudio thread) ───────────────────────────

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_paused || _cts?.IsCancellationRequested == true) return;

        // Write raw bytes into the ring buffer for potential post-wake retrieval
        WriteRing(e.Buffer, e.BytesRecorded);

        if (_capturing)
        {
            HandleCapture(e.Buffer, e.BytesRecorded);
            return;
        }

        // Accumulate into 80ms frame
        int src = 0;
        while (src < e.BytesRecorded)
        {
            int copy = Math.Min(FrameBytes - _framePos, e.BytesRecorded - src);
            Buffer.BlockCopy(e.Buffer, src, _frameAccum, _framePos, copy);
            _framePos += copy;
            src       += copy;

            if (_framePos >= FrameBytes)
            {
                ProcessFrame(_frameAccum);
                _framePos = 0;
            }
        }
    }

    private void ProcessFrame(byte[] frameBytes)
    {
        if (_session is null || _melSession is null) return;

        // Convert 16-bit PCM → float32 normalised [-1, 1]
        var samples = new float[FrameSamples];
        for (int i = 0; i < FrameSamples; i++)
            samples[i] = BitConverter.ToInt16(frameBytes, i * 2) / 32768f;

        // Run mel spectrogram extractor
        var audioTensor = new DenseTensor<float>(samples, [1, FrameSamples]);
        using var melResult = _melSession.Run(
        [
            NamedOnnxValue.CreateFromTensor("input", audioTensor)
        ]);

        var melFrame = melResult.First().AsEnumerable<float>().ToArray(); // [1, 1, 32]

        // Shift mel history left by 1 and append new frame
        for (int t = 0; t < MelFrames - 1; t++)
            for (int f = 0; f < MelFeatures; f++)
                _melHistory[0, t, f] = _melHistory[0, t + 1, f];

        for (int f = 0; f < MelFeatures && f < melFrame.Length; f++)
            _melHistory[0, MelFrames - 1, f] = melFrame[f];

        // Build mel input tensor [1, 76, 32]
        var flat = new float[MelFrames * MelFeatures];
        for (int t = 0; t < MelFrames; t++)
            for (int f = 0; f < MelFeatures; f++)
                flat[t * MelFeatures + f] = _melHistory[0, t, f];

        var melTensor = new DenseTensor<float>(flat, [1, MelFrames, MelFeatures]);

        // Run wake word model
        using var wakeResult = _session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input", melTensor)
        ]);

        float score = wakeResult.First().AsEnumerable<float>().First();

        if (score >= WakeThreshold)
        {
            _log.LogInformation("Wake word detected! Score={Score:F3}", score);
            BeginCapture();
        }
    }

    // ── Post-wake capture (VAD-based) ─────────────────────────────────────────

    private void BeginCapture()
    {
        _capturing        = true;
        _silenceSamples   = 0;
        _capturedSamples  = 0;
        _captureStream    = new MemoryStream();
        _captureWriter    = new WaveFileWriter(_captureStream, WavFmt);

        // Prepend last ~0.5s of ring buffer so we catch any partial word after wake word
        int prePadBytes = Math.Min(16000 * 2 / 2, RingBytes); // 0.5s
        var pre = ReadRingLast(prePadBytes);
        _captureWriter.Write(pre, 0, pre.Length);
        _capturedSamples += pre.Length / 2;
    }

    private void HandleCapture(byte[] buf, int count)
    {
        if (_captureWriter is null || _captureStream is null) return;

        _captureWriter.Write(buf, 0, count);
        _capturedSamples += count / 2;

        // VAD: measure RMS energy of this chunk
        double rms = 0;
        for (int i = 0; i + 1 < count; i += 2)
        {
            float s = BitConverter.ToInt16(buf, i) / 32768f;
            rms += s * s;
        }
        rms = Math.Sqrt(rms / (count / 2));

        int silenceThreshSamples = WavFmt.SampleRate * SilenceMs / 1000;
        int captureMaxSamples    = WavFmt.SampleRate * CaptureMaxMs / 1000;

        if (rms < SilenceEnergy)
            _silenceSamples += count / 2;
        else
            _silenceSamples = 0;

        bool silenceMet  = _silenceSamples  >= silenceThreshSamples;
        bool maxReached  = _capturedSamples >= captureMaxSamples;

        if (silenceMet || maxReached)
        {
            _capturing = false;
            _captureWriter.Flush();
            var wavBytes = _captureStream.ToArray();
            _captureWriter.Dispose();
            _captureStream.Dispose();
            _captureWriter = null;
            _captureStream = null;

            _log.LogInformation("Post-wake capture done: {Bytes} bytes ({Reason})",
                wavBytes.Length, silenceMet ? "silence" : "max length");

            // Fire on thread pool — don't block NAudio callback
            Task.Run(() => WakeWordDetected?.Invoke(this, wavBytes));
        }
    }

    // ── Ring buffer helpers ────────────────────────────────────────────────────

    private void WriteRing(byte[] buf, int count)
    {
        for (int i = 0; i < count; i++)
        {
            _ring[_ringPos] = buf[i];
            _ringPos = (_ringPos + 1) % RingBytes;
        }
    }

    private byte[] ReadRingLast(int byteCount)
    {
        byteCount = Math.Min(byteCount, RingBytes);
        var result = new byte[byteCount];
        int start  = (_ringPos - byteCount + RingBytes) % RingBytes;
        for (int i = 0; i < byteCount; i++)
            result[i] = _ring[(start + i) % RingBytes];
        return result;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _session?.Dispose();
        _melSession?.Dispose();
        _captureWriter?.Dispose();
        _captureStream?.Dispose();
        _cts?.Dispose();
    }
}
