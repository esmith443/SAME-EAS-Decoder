using SameEasDecoder.Core.Audio;

namespace SameEasDecoder.Core.Same;

public sealed record AlertRecording(string Path, TimeSpan Duration, SameAlert? Alert, DateTime StartedUtc);

public sealed class AlertRecorder
{
    private readonly int _rate;
    private readonly float[] _preroll;
    private readonly int _maxSamples;
    private int _preWrite;
    private long _preCount;
    private List<float>? _active;
    private long _tailRemaining = -1;
    private SameAlert? _alert;
    private DateTime _startedUtc;

    public AlertRecorder(int sampleRate, double prerollSeconds = 6, double maxSeconds = 240)
    {
        _rate = sampleRate;
        _preroll = new float[(int)(sampleRate * prerollSeconds)];
        _maxSamples = (int)(sampleRate * maxSeconds);
    }

    public bool Enabled { get; set; }
    public string? Directory { get; set; }
    public bool IsRecording => _active is not null;

    public event Action<AlertRecording>? Saved;
    public event Action<string>? Failed;

    public void Process(ReadOnlySpan<float> block)
    {
        foreach (float s in block)
        {
            _preroll[_preWrite] = s;
            if (++_preWrite == _preroll.Length) _preWrite = 0;
        }
        _preCount += block.Length;

        if (_active is null) return;
        foreach (float s in block) _active.Add(s);

        if (_tailRemaining >= 0)
        {
            _tailRemaining -= block.Length;
            if (_tailRemaining <= 0) Finish();
        }
        else if (_active.Count >= _maxSamples)
        {
            Finish();
        }
    }

    public void OnHeaderBurst()
    {
        if (!Enabled || string.IsNullOrEmpty(Directory)) return;
        if (_active is null) Begin();
        else if (_tailRemaining >= 0) { Finish(); Begin(); }
    }

    public void OnAlert(SameAlert alert)
    {
        if (_active is not null) _alert ??= alert;
    }

    public void OnEndOfMessage()
    {
        if (_active is not null && _tailRemaining < 0) _tailRemaining = _rate;
    }

    public void Flush()
    {
        if (_active is not null) Finish();
    }

    public void Reset()
    {
        _active = null;
        _tailRemaining = -1;
        _alert = null;
        _preCount = 0;
        _preWrite = 0;
        Array.Clear(_preroll);
    }

    private void Begin()
    {
        int have = (int)Math.Min(_preCount, _preroll.Length);
        var buffer = new List<float>(_rate * 60);
        int start = (_preWrite - have + _preroll.Length) % _preroll.Length;
        for (int i = 0; i < have; i++) buffer.Add(_preroll[(start + i) % _preroll.Length]);
        _active = buffer;
        _alert = null;
        _tailRemaining = -1;
        _startedUtc = DateTime.UtcNow - TimeSpan.FromSeconds(have / (double)_rate);
    }

    private void Finish()
    {
        var samples = _active!.ToArray();
        var alert = _alert;
        var started = _startedUtc;
        string? dir = Directory;
        _active = null;
        _tailRemaining = -1;
        _alert = null;
        if (dir is null) return;

        int rate = _rate;
        Task.Run(() =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                string label = alert is null ? "unparsed" : $"{alert.Header.EventCode} {alert.Header.OriginatorCode}";
                string name = $"{started.ToLocalTime():yyyy-MM-dd HHmmss} {label}.wav";
                string path = Path.Combine(dir, name);
                for (int n = 2; File.Exists(path); n++) path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({n}).wav");
                WavFile.WriteMono16(path, samples, rate);
                Saved?.Invoke(new AlertRecording(path, TimeSpan.FromSeconds(samples.Length / (double)rate), alert, started));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failed?.Invoke(ex.Message);
            }
        });
    }
}
