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
///    → 1280-sample (80ms) audio chunks
///    → melspectrogram.onnx: [1, 1280] → [1, 1, 5, 32] mel features
///    → rolling 48-frame mel buffer maintained in RAM
///    → reshape [48, 32] → [1, 16, 96] → hey_jarvis_v0.1.onnx → score
///    → score > threshold → VAD capture post-wake audio
///    → return captured WAV bytes to ZeroHost for Whisper STT
///
/// Audio is NEVER written to disk. Only a 3s ring buffer lives in RAM.
/// Models are auto-downloaded from GitHub releases on first run.
/// </summary>
public sealed class WakeWordListener : IAsyncDisposable
{
    // ── Events ────────────────────────────────────────────────────────────────
    /// <summary>Fired on thread pool with the captured post-wake PCM WAV bytes.</summary>
    public event EventHandler<byte[]>? WakeWordDetected;

    // ── Audio constants ───────────────────────────────────────────────────────
    private static readonly WaveFormat WavFmt = new(16000, 16, 1);
    private const int FrameSamples  = 1280;           // 80ms at 16kHz
    private const int FrameBytes    = FrameSamples * 2; // 16-bit
    private const int RingSeconds   = 3;
    private const int RingBytes     = 16000 * 2 * RingSeconds;

    // ── OWW pipeline constants ────────────────────────────────────────────────
    // melspectrogram: [1, 1280] → [1, 1, 5, 32]  (5 mel frames per audio chunk)
    // hey_jarvis:     [1, 16, 96]                 (16 windows × 96 = 3×32 stacked)
    private const int MelPerChunk  = 5;   // mel time-steps produced per 1280-sample chunk
    private const int MelFeatures  = 32;
    private const int WwWindows    = 16;
    private const int WwStackSize  = 3;                         // mel frames stacked per window
    private const int MelBufSize   = WwWindows * WwStackSize;   // 48 mel frames total

    // ── Capture / VAD ─────────────────────────────────────────────────────────
    private const int CaptureMaxMs   = 6000;
    private const int SilenceMs      = 1200;
    private const double SilenceRms  = 0.005;

    // ── Model URLs ────────────────────────────────────────────────────────────
    private static readonly string ModelDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ZERO", "models");

    private const string MelFileName = "melspectrogram.onnx";
    private const string WwFileName  = "hey_jarvis_v0.1.onnx";
    private const string MelUrl = "https://github.com/dscripka/openWakeWord/releases/download/v0.5.1/melspectrogram.onnx";
    private const string WwUrl  = "https://github.com/dscripka/openWakeWord/releases/download/v0.5.1/hey_jarvis_v0.1.onnx";

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly ZeroConfig                _cfg;
    private readonly ILogger<WakeWordListener> _log;

    private InferenceSession? _melSession;
    private InferenceSession? _wwSession;

    // Rolling mel feature buffer [MelBufSize, MelFeatures]
    private readonly float[] _melBuf = new float[MelBufSize * MelFeatures];

    // Audio ring buffer (raw PCM, never written to disk)
    private readonly byte[] _ring    = new byte[RingBytes];
    private int              _ringPos;

    // Audio frame accumulator
    private readonly byte[] _frameAccum = new byte[FrameBytes];
    private int              _framePos;

    // Post-wake capture state
    private volatile bool   _paused;
    private volatile bool   _capturing;
    private MemoryStream?   _captureStream;
    private WaveFileWriter? _captureWriter;
    private int             _silenceSamples;
    private int             _capturedSamples;

    private WaveInEvent?              _waveIn;
    private CancellationTokenSource?  _cts;

    public WakeWordListener(IOptions<ZeroConfig> cfg, ILogger<WakeWordListener> log)
    {
        _cfg = cfg.Value;
        _log = log;
    }

    // ── Initialise ────────────────────────────────────────────────────────────

    public async Task InitialiseAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(ModelDir);
        await EnsureModelAsync(MelFileName, MelUrl, ct);
        await EnsureModelAsync(WwFileName,  WwUrl,  ct);

        var opts = new SessionOptions();
        opts.AppendExecutionProvider_CPU();

        _melSession = new InferenceSession(Path.Combine(ModelDir, MelFileName), opts);
        _wwSession  = new InferenceSession(Path.Combine(ModelDir, WwFileName),  opts);

        _log.LogInformation("OpenWakeWord ready (model={Model}, threshold={T})",
            WwFileName, _cfg.WakeWordThreshold);
    }

    private async Task EnsureModelAsync(string fileName, string url, CancellationToken ct)
    {
        var path = Path.Combine(ModelDir, fileName);
        if (File.Exists(path)) return;

        _log.LogInformation("Downloading wake word model: {File}...", fileName);
        using var http  = new HttpClient();
        await using var resp = await http.GetStreamAsync(url, ct);
        await using var fs   = File.OpenWrite(path);
        await resp.CopyToAsync(fs, ct);
        _log.LogInformation("Wake word model downloaded: {File}", fileName);
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken ct = default)
    {
        if (!_cfg.EnableWakeWord || _wwSession is null) return Task.CompletedTask;

        _cts    = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _waveIn = new WaveInEvent { WaveFormat = WavFmt, BufferMilliseconds = 40 };
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

    public void Pause()  => _paused = true;

    public void Resume()
    {
        _capturing = false;
        _captureWriter?.Dispose();
        _captureStream?.Dispose();
        _captureWriter = null;
        _captureStream = null;
        _paused        = false;
    }

    // ── NAudio data handler ───────────────────────────────────────────────────

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_paused || _cts?.IsCancellationRequested == true) return;

        WriteRing(e.Buffer, e.BytesRecorded);

        if (_capturing)
        {
            HandleCapture(e.Buffer, e.BytesRecorded);
            return;
        }

        // Accumulate into 1280-sample frames
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

    // ── OWW inference pipeline ────────────────────────────────────────────────

    private void ProcessFrame(byte[] frameBytes)
    {
        if (_melSession is null || _wwSession is null) return;

        // 1. Convert PCM 16-bit → float32 normalised [-1, 1]
        var audio = new float[FrameSamples];
        for (int i = 0; i < FrameSamples; i++)
            audio[i] = BitConverter.ToInt16(frameBytes, i * 2) / 32768f;

        // 2. Run melspectrogram: [1, 1280] → [1, 1, 5, 32]
        var audioTensor = new DenseTensor<float>(audio, [1, FrameSamples]);
        float[] melFrames;
        try
        {
            using var melResult = _melSession.Run(
                [NamedOnnxValue.CreateFromTensor("input", audioTensor)]);
            melFrames = melResult.First().AsEnumerable<float>().ToArray(); // 5*32 = 160 values
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Melspectrogram inference failed.");
            return;
        }

        // 3. Shift mel buffer left by MelPerChunk (5) rows, append new frames
        //    Buffer layout: [MelBufSize * MelFeatures] = flat row-major [48, 32]
        int shiftBytes = (MelBufSize - MelPerChunk) * MelFeatures;
        Buffer.BlockCopy(_melBuf, MelPerChunk * MelFeatures * sizeof(float),
                         _melBuf, 0,
                         shiftBytes * sizeof(float));
        Buffer.BlockCopy(melFrames, 0,
                         _melBuf, shiftBytes * sizeof(float),
                         melFrames.Length * sizeof(float));

        // 4. Reshape [48, 32] → [1, 16, 96]  (stack 3 consecutive mel rows per window)
        var wwInput = new float[1 * WwWindows * (WwStackSize * MelFeatures)];
        for (int w = 0; w < WwWindows; w++)
        {
            int srcRow = w * WwStackSize;
            int dstOff = w * WwStackSize * MelFeatures;
            Buffer.BlockCopy(_melBuf, srcRow * MelFeatures * sizeof(float),
                             wwInput, dstOff * sizeof(float),
                             WwStackSize * MelFeatures * sizeof(float));
        }

        // 5. Run hey_jarvis: [1, 16, 96] → [1, 1] score
        float score;
        try
        {
            var wwTensor = new DenseTensor<float>(wwInput, [1, WwWindows, WwStackSize * MelFeatures]);
            using var wwResult = _wwSession.Run(
                [NamedOnnxValue.CreateFromTensor("x.1", wwTensor)]);
            score = wwResult.First().AsEnumerable<float>().First();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Wake word inference failed.");
            return;
        }

        if (score >= (float)_cfg.WakeWordThreshold)
        {
            _log.LogInformation("Wake word detected! Score={Score:F3}", score);
            BeginCapture();
        }
    }

    // ── Post-wake VAD capture ─────────────────────────────────────────────────

    private void BeginCapture()
    {
        _capturing       = true;
        _silenceSamples  = 0;
        _capturedSamples = 0;
        _captureStream   = new MemoryStream();
        _captureWriter   = new WaveFileWriter(_captureStream, WavFmt);

        // Prepend last 0.5s of ring buffer (catches any trailing command words)
        var pre = ReadRingLast(16000 * 2 / 2);
        _captureWriter.Write(pre, 0, pre.Length);
        _capturedSamples += pre.Length / 2;
    }

    private void HandleCapture(byte[] buf, int count)
    {
        if (_captureWriter is null || _captureStream is null) return;

        _captureWriter.Write(buf, 0, count);
        _capturedSamples += count / 2;

        // Measure RMS energy for VAD
        double rms = 0;
        for (int i = 0; i + 1 < count; i += 2)
        {
            float s = BitConverter.ToInt16(buf, i) / 32768f;
            rms += s * s;
        }
        rms = Math.Sqrt(rms / (count / 2));

        if (rms < SilenceRms) _silenceSamples += count / 2;
        else                   _silenceSamples  = 0;

        bool silenceMet = _silenceSamples  >= WavFmt.SampleRate * SilenceMs / 1000;
        bool maxReached = _capturedSamples >= WavFmt.SampleRate * CaptureMaxMs / 1000;

        if (!silenceMet && !maxReached) return;

        _capturing = false;
        _captureWriter.Flush();
        var wavBytes = _captureStream.ToArray();
        _captureWriter.Dispose();
        _captureStream.Dispose();
        _captureWriter = null;
        _captureStream = null;

        _log.LogInformation("Post-wake capture: {Bytes} bytes ({Reason})",
            wavBytes.Length, silenceMet ? "silence" : "max");

        Task.Run(() => WakeWordDetected?.Invoke(this, wavBytes));
    }

    // ── Ring buffer ───────────────────────────────────────────────────────────

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
        _melSession?.Dispose();
        _wwSession?.Dispose();
        _captureWriter?.Dispose();
        _captureStream?.Dispose();
        _cts?.Dispose();
    }
}
