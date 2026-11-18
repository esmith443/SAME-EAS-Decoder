using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SameEasDecoder.Core.Dsp;
using SameEasDecoder.Core.Same;

namespace SameEasDecoder.Core;

public enum ChannelMode { Mix, Left, Right }

public sealed record LevelSnapshot(float[] Peak, float[] Rms, long Sequence);

public sealed class DecoderPipeline : IDisposable
{
    public const int DecodeRate = 16000;

    private readonly object _sync = new();
    private readonly SameDemodulator _same = new(DecodeRate);
    private readonly SameMessageAssembler _assembler = new(DecodeRate);
    private readonly AttentionToneDetector _tones = new(DecodeRate);
    private readonly SpectrumAnalyzer _spectrum = new(1024, 1024);
    private readonly AlertRecorder _recorder = new(DecodeRate);
    private readonly ConcurrentQueue<float[]> _frames = new();
    private readonly List<float> _resampled = new(16384);
    private StreamingResampler? _resampler;
    private float[] _mono = new float[8192];
    private int _channels = 1;
    private bool _running;
    private long _levelSeq;
    private volatile LevelSnapshot _levels = new([0f], [0f], 0);

    public DecoderPipeline()
    {
        _same.BurstDecoded += b =>
        {
            if (b.Kind == SameBurstKind.Header) _recorder.OnHeaderBurst();
            SameBurstDecoded?.Invoke(b);
            _assembler.AddBurst(b);
        };
        _same.CarrierChanged += c => SameCarrierChanged?.Invoke(c);
        _same.SyncFailed += f => SameSyncFailed?.Invoke(f);
        _assembler.AlertDecoded += a => { _recorder.OnAlert(a); SameAlertDecoded?.Invoke(a); };
        _assembler.AlertProgress += a => SameAlertProgress?.Invoke(a);
        _assembler.EndOfMessage += e => { _recorder.OnEndOfMessage(); SameEndOfMessage?.Invoke(e); };
        _assembler.HeaderRejected += (t, n) => SameHeaderRejected?.Invoke(t, n);
        _tones.ToneStarted += k => AttentionToneStarted?.Invoke(k);
        _tones.ToneEnded += (k, d) => AttentionToneEnded?.Invoke(k, d);
        _recorder.Saved += r => RecordingSaved?.Invoke(r);
        _recorder.Failed += m => Diagnostic?.Invoke($"Could not save alert recording: {m}");
        _spectrum.FrameReady += f =>
        {
            _frames.Enqueue(f);
            while (_frames.Count > 64) _frames.TryDequeue(out _);
        };
    }

    public ChannelMode ChannelMode { get; set; } = ChannelMode.Mix;

    public bool RecordingEnabled
    {
        get => _recorder.Enabled;
        set => _recorder.Enabled = value;
    }

    public string? RecordingDirectory
    {
        get => _recorder.Directory;
        set => _recorder.Directory = value;
    }

    public bool IsRunning => _running;
    public bool IsSameCarrier => _same.CarrierDetected || _same.IsSynced;
    public double SameSignalQuality => _same.SignalQuality;
    public AttentionToneKind AttentionTone => _tones.Active;
    public bool IsRecording => _recorder.IsRecording;
    public LevelSnapshot Levels => _levels;
    public int InputSampleRate { get; private set; }
    public int InputChannels => _channels;

    public TimeSpan StreamPosition => TimeSpan.FromSeconds(_same.SamplePosition / (double)DecodeRate);

    public event Action<SameAlert>? SameAlertDecoded;

    public event Action<SameAlert>? SameAlertProgress;
    public event Action<SameEndOfMessage>? SameEndOfMessage;
    public event Action<SameBurst>? SameBurstDecoded;
    public event Action<string, IReadOnlyList<string>>? SameHeaderRejected;
    public event Action<bool>? SameCarrierChanged;

    public event Action<SameSyncFailure>? SameSyncFailed;
    public event Action<AttentionToneKind>? AttentionToneStarted;
    public event Action<AttentionToneKind, TimeSpan>? AttentionToneEnded;
    public event Action<AlertRecording>? RecordingSaved;
    public event Action<string>? Diagnostic;

    public bool TryDequeueSpectrum(out float[] frame) => _frames.TryDequeue(out frame!);

    public void Start(int inputSampleRate, int channels)
    {
        lock (_sync)
        {
            if (_running) StopCore();
            InputSampleRate = inputSampleRate;
            _channels = Math.Max(1, channels);
            _resampler = new StreamingResampler(inputSampleRate, DecodeRate);
            _same.Reset();
            _assembler.Reset();
            _tones.Reset();
            _spectrum.Reset();
            _recorder.Reset();
            _frames.Clear();
            _levels = new LevelSnapshot(new float[Math.Min(2, _channels)], new float[Math.Min(2, _channels)], ++_levelSeq);
            _running = true;
        }
    }

    public void Stop()
    {
        lock (_sync) StopCore();
    }

    private void StopCore()
    {
        if (!_running) return;
        _running = false;
        _assembler.Flush();
        _tones.Reset();
        _recorder.Flush();
        _levels = new LevelSnapshot(new float[_levels.Peak.Length], new float[_levels.Rms.Length], ++_levelSeq);
    }

    public void Process(ReadOnlySpan<float> interleaved)
    {
        lock (_sync)
        {
            if (!_running || _resampler is null) return;
            int ch = _channels;
            int frames = interleaved.Length / ch;
            if (frames == 0) return;

            UpdateLevels(interleaved, ch, frames);

            if (_mono.Length < frames) _mono = new float[frames * 2];
            var mode = ChannelMode;
            for (int f = 0; f < frames; f++)
            {
                int o = f * ch;
                _mono[f] = ch == 1 ? interleaved[o] : mode switch
                {
                    ChannelMode.Left => interleaved[o],
                    ChannelMode.Right => interleaved[o + 1],
                    _ => (interleaved[o] + interleaved[o + 1]) * 0.5f,
                };
            }

            _resampled.Clear();
            _resampler.Process(_mono.AsSpan(0, frames), _resampled);
            Decode(CollectionsMarshal.AsSpan(_resampled));
        }
    }

    public void ProcessSilence(TimeSpan duration)
    {
        lock (_sync)
        {
            if (!_running) return;
            int n = (int)(duration.TotalSeconds * DecodeRate);
            if (n <= 0) return;
            _levels = new LevelSnapshot(new float[_levels.Peak.Length], new float[_levels.Rms.Length], ++_levelSeq);
            Decode(new float[n]);
        }
    }

    private void Decode(ReadOnlySpan<float> block)
    {
        if (block.IsEmpty) return;
        _spectrum.Process(block);
        _recorder.Process(block);
        _same.Process(block);
        _tones.Process(block);
        _assembler.Advance(_same.SamplePosition);
    }

    private void UpdateLevels(ReadOnlySpan<float> data, int ch, int frames)
    {
        int shown = Math.Min(2, ch);
        var peak = new float[shown];
        var rms = new float[shown];
        for (int c = 0; c < shown; c++)
        {
            float p = 0;
            double sum = 0;
            for (int f = 0; f < frames; f++)
            {
                float v = data[f * ch + c];
                float a = Math.Abs(v);
                if (a > p) p = a;
                sum += v * v;
            }
            peak[c] = p;
            rms[c] = (float)Math.Sqrt(sum / frames);
        }
        _levels = new LevelSnapshot(peak, rms, ++_levelSeq);
    }

    public void Dispose() => Stop();
}
