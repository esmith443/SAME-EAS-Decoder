namespace SameEasDecoder.Core.Same;

public enum AttentionToneKind { None, EasDualTone, NwrTone }

public sealed class AttentionToneDetector
{
    private const int StartBlocks = 8;
    private const int EndBlocks = 3;

    private readonly int _block;
    private readonly double _c853, _c960, _c1050;
    private double _s853a, _s853b, _s960a, _s960b, _s1050a, _s1050b, _energy;
    private int _fill;
    private AttentionToneKind _candidate;
    private int _candidateBlocks, _missBlocks;
    private long _blocks, _startBlock;

    public AttentionToneDetector(int sampleRate)
    {
        SampleRate = sampleRate;
        _block = sampleRate / 10;
        _c853 = Coefficient(853, sampleRate);
        _c960 = Coefficient(960, sampleRate);
        _c1050 = Coefficient(1050, sampleRate);
    }

    public int SampleRate { get; }
    public AttentionToneKind Active { get; private set; }

    public event Action<AttentionToneKind>? ToneStarted;
    public event Action<AttentionToneKind, TimeSpan>? ToneEnded;

    private static double Coefficient(double f, int fs) => 2 * Math.Cos(2 * Math.PI * f / fs);

    public void Reset()
    {
        _s853a = _s853b = _s960a = _s960b = _s1050a = _s1050b = _energy = 0;
        _fill = 0; _candidate = AttentionToneKind.None; _candidateBlocks = 0; _missBlocks = 0;
        if (Active != AttentionToneKind.None) End();
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (float x in samples)
        {
            double s = x + _c853 * _s853a - _s853b; _s853b = _s853a; _s853a = s;
            s = x + _c960 * _s960a - _s960b; _s960b = _s960a; _s960a = s;
            s = x + _c1050 * _s1050a - _s1050b; _s1050b = _s1050a; _s1050a = s;
            _energy += x * x;
            if (++_fill == _block) EvaluateBlock();
        }
    }

    private void EvaluateBlock()
    {
        double norm = _energy * _block * 0.5;
        double p853 = 0, p960 = 0, p1050 = 0;
        double rms = Math.Sqrt(_energy / _block);
        if (norm > 1e-12 && rms > 0.0015)
        {
            p853 = Power(_s853a, _s853b, _c853) / norm;
            p960 = Power(_s960a, _s960b, _c960) / norm;
            p1050 = Power(_s1050a, _s1050b, _c1050) / norm;
        }
        _s853a = _s853b = _s960a = _s960b = _s1050a = _s1050b = _energy = 0;
        _fill = 0;
        _blocks++;

        var kind = AttentionToneKind.None;
        if (p853 > 0.18 && p960 > 0.18 && p853 + p960 > 0.6) kind = AttentionToneKind.EasDualTone;
        else if (p1050 > 0.6) kind = AttentionToneKind.NwrTone;

        if (Active != AttentionToneKind.None)
        {
            if (kind == Active) { _missBlocks = 0; return; }
            if (++_missBlocks >= EndBlocks) End();
            return;
        }

        if (kind == AttentionToneKind.None) { _candidate = kind; _candidateBlocks = 0; return; }
        if (kind != _candidate) { _candidate = kind; _candidateBlocks = 0; }
        if (++_candidateBlocks >= StartBlocks)
        {
            Active = kind;
            _missBlocks = 0;
            _startBlock = _blocks - _candidateBlocks;
            ToneStarted?.Invoke(kind);
        }
    }

    private void End()
    {
        var kind = Active;
        Active = AttentionToneKind.None;
        _candidate = AttentionToneKind.None;
        _candidateBlocks = 0;
        long blocks = Math.Max(0, _blocks - _startBlock - _missBlocks);
        ToneEnded?.Invoke(kind, TimeSpan.FromSeconds(blocks / 10.0));
    }

    private static double Power(double s1, double s2, double c) => s1 * s1 + s2 * s2 - c * s1 * s2;
}
