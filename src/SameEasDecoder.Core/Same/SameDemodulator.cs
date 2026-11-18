using System.Text;

namespace SameEasDecoder.Core.Same;

public enum SameBurstKind { Header, EndOfMessage, Unknown }

public sealed record SameDemodulatorOptions
{
    public bool BandPass { get; init; } = true;
    public bool InterpolateCrossings { get; init; } = true;
    public bool TrackFrequency { get; init; } = true;
    public bool TolerantPreamble { get; init; } = true;
    public double HuntGain { get; init; } = 0.30;
    public double DataGain { get; init; } = 0.10;
    public double HuntRateGain { get; init; } = 0.02;
    public double DataRateGain { get; init; } = 0.003;
    public double WindowFraction { get; init; } = 1.0;
    public double DecisionSpread { get; init; } = 0.0;

    public static SameDemodulatorOptions Default { get; } = new();

    public static SameDemodulatorOptions Legacy { get; } = new()
    {
        BandPass = false, InterpolateCrossings = false, TrackFrequency = false, TolerantPreamble = false,
        HuntGain = 0.35, DataGain = 0.12,
    };
}

public sealed record SameSyncFailure(string Reason, int PreambleBytes, byte[] RawBytes, double Quality, long StartSample, long EndSample);

public sealed record SameBurst(string Text, SameBurstKind Kind, long StartSample, long EndSample, int ErrorCount, int PreambleBytes)
{
    public byte[] RawBytes { get; init; } = [];

    public float[] SoftBits { get; init; } = [];
    public double Quality { get; init; }

    public static bool IsValidByte(byte b) => b >= 0x20 && b <= 0x7E;

    public IReadOnlyList<int> DamagedPositions =>
        Enumerable.Range(0, RawBytes.Length).Where(i => !IsValidByte(RawBytes[i])).ToArray();

    public static SameBurstKind Classify(string text)
    {
        if (text.Length < 4) return SameBurstKind.Unknown;
        int z = 0, n = 0, zBits = 0, nBits = 0;
        for (int i = 0; i < 4; i++)
        {
            if (text[i] == "ZCZC"[i]) z++;
            if (text[i] == 'N') n++;
            zBits += System.Numerics.BitOperations.PopCount((uint)(text[i] ^ "ZCZC"[i]) & 0xFF);
            nBits += System.Numerics.BitOperations.PopCount((uint)(text[i] ^ 'N') & 0xFF);
        }
        if (z >= 3 || (zBits <= 3 && zBits < nBits)) return SameBurstKind.Header;
        if (n >= 3 || nBits <= 3) return SameBurstKind.EndOfMessage;
        return SameBurstKind.Unknown;
    }
}

public sealed class SameDemodulator
{
    public const double BaudRate = 520.0 + 5.0 / 6.0;
    public const double MarkFrequency = 2083.0 + 1.0 / 3.0;
    public const double SpaceFrequency = 1562.5;
    public const byte PreambleByte = 0xAB;

    private const int MaxBurstLength = 300;
    private const double CarrierOnQuality = 0.50;
    private const double CarrierOffQuality = 0.30;
    private const double SyncQuality = 0.30;
    private const double DataLostQuality = 0.20;

    private enum State { Hunting, Preamble, Data }

    private readonly SameDemodulatorOptions _opt;
    private readonly Dsp.Biquad[] _bandPass;
    private readonly int _n;
    private readonly float[] _mc, _ms, _sc, _ss, _hist, _rawHist;
    private readonly double _phaseInc, _qualityAlpha, _samplesPerBit;
    private int _w;
    private double _phase, _quality, _freqOffset, _prevD, _crossTarget = 0.5;
    private readonly double[] _dRing;
    private int _dPos;
    private double _dSum;
    private readonly double _toneGain = 1;
    private int _lastSign = 1;
    private bool _carrier;

    private State _state;
    private int _shift, _bitCount, _preambleBytes, _consecutiveErrors, _errorCount;
    private int _plusIndex = -1, _dashesAfterPlus, _lostBits;
    private long _sample, _burstStart;
    private readonly StringBuilder _text = new();
    private readonly List<byte> _raw = [];
    private readonly List<float> _soft = [];
    private readonly float[] _lastBits = new float[8];
    private int _lastBitPos;
    private double _marginSum;
    private long _carrierStart;
    private bool _syncedThisCarrier;
    private double _carrierQualitySum;
    private long _carrierQualityCount;
    private int _marginCount;

    public SameDemodulator(int sampleRate, SameDemodulatorOptions? options = null)
    {
        SampleRate = sampleRate;
        _opt = options ?? SameDemodulatorOptions.Default;
        _bandPass = _opt.BandPass
            ? [Dsp.Biquad.HighPass(sampleRate, 900), Dsp.Biquad.HighPass(sampleRate, 900),
               Dsp.Biquad.LowPass(sampleRate, 3000), Dsp.Biquad.LowPass(sampleRate, 3000)]
            : [];
        if (_bandPass.Length > 0)
            _toneGain = (MeasurePowerGain(sampleRate, MarkFrequency) + MeasurePowerGain(sampleRate, SpaceFrequency)) / 2;
        _samplesPerBit = sampleRate / BaudRate;
        _n = Math.Max(8, (int)Math.Round(_samplesPerBit * Math.Clamp(_opt.WindowFraction, 0.3, 1.0)));
        int spread = Math.Max(1, (int)Math.Round(_samplesPerBit * Math.Clamp(_opt.DecisionSpread, 0, 0.6)));
        _dRing = new double[spread];
        _crossTarget = 0.5 - (spread - 1) / (2 * _samplesPerBit);
        _mc = new float[_n]; _ms = new float[_n]; _sc = new float[_n]; _ss = new float[_n];
        for (int k = 0; k < _n; k++)
        {
            double pm = 2 * Math.PI * MarkFrequency * k / sampleRate;
            double ps = 2 * Math.PI * SpaceFrequency * k / sampleRate;
            _mc[k] = (float)Math.Cos(pm); _ms[k] = (float)Math.Sin(pm);
            _sc[k] = (float)Math.Cos(ps); _ss[k] = (float)Math.Sin(ps);
        }
        _hist = new float[_n * 2];
        _rawHist = new float[_n * 2];
        _phaseInc = BaudRate / sampleRate;
        _qualityAlpha = 1.0 / (2 * _samplesPerBit);
    }

    public int SampleRate { get; }

    public bool CarrierDetected => _carrier;

    public double SignalQuality => _quality;

    public long SamplePosition => _sample;

    public event Action<SameBurst>? BurstDecoded;

    public event Action<SameSyncFailure>? SyncFailed;

    public bool IsSynced => _state != State.Hunting;
    public event Action<bool>? CarrierChanged;

    public void Reset()
    {
        Array.Clear(_hist);
        Array.Clear(_rawHist);
        Array.Clear(_dRing);
        _dSum = 0; _dPos = 0;
        _w = 0; _phase = 0; _quality = 0; _lastSign = 1; _freqOffset = 0; _prevD = 0;
        foreach (var f in _bandPass) f.Reset();
        if (_carrier) { _carrier = false; CarrierChanged?.Invoke(false); }
        ResetFraming();
        _sample = 0;
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        for (int i = 0; i < samples.Length; i++) Step(samples[i]);
    }

    public double ClockOffset => _freqOffset;

    private void Step(float x)
    {
        float raw = x;
        foreach (var f in _bandPass) x = f.Process(x);
        _hist[_w] = x;
        _hist[_w + _n] = x;
        _rawHist[_w] = raw;
        _rawHist[_w + _n] = raw;
        if (++_w == _n) _w = 0;
        _sample++;

        double mi = 0, mq = 0, si = 0, sq = 0, e = 0;
        int o = _w;
        for (int k = 0; k < _n; k++)
        {
            float v = _hist[o + k];
            mi += v * _mc[k]; mq += v * _ms[k];
            si += v * _sc[k]; sq += v * _ss[k];
            float r = _rawHist[o + k];
            e += r * r;
        }
        double m = mi * mi + mq * mq, s = si * si + sq * sq, tot = m + s;
        double d = tot > 1e-20 ? (m - s) / tot : 0;
        double q = e > 1e-12 ? Math.Min(1.5, tot / (e * _n * 0.5 * _toneGain)) : 0;
        _quality += (q - _quality) * _qualityAlpha;

        if (!_carrier && _quality > CarrierOnQuality)
        {
            _carrier = true;
            _carrierStart = _sample;
            _syncedThisCarrier = _state != State.Hunting;
            _carrierQualitySum = 0;
            _carrierQualityCount = 0;
            CarrierChanged?.Invoke(true);
        }
        else if (_carrier && _quality < CarrierOffQuality)
        {
            _carrier = false;
            CarrierChanged?.Invoke(false);
            ReportCarrierWithoutSync();
        }
        if (_carrier)
        {
            _carrierQualitySum += _quality;
            _carrierQualityCount++;
        }

        double inc = _phaseInc * (1 + _freqOffset);
        _phase += inc;
        int sign = d >= 0 ? 1 : -1;
        if (sign != _lastSign)
        {
            _lastSign = sign;
            double crossPhase = _phase, weight = 1;
            if (_opt.InterpolateCrossings)
            {
                double step = _prevD - d;
                double frac = Math.Abs(step) > 1e-9 ? Math.Clamp(_prevD / step, 0, 1) : 1;
                crossPhase = _phase - inc * (1 - frac);
                weight = Math.Min(1, Math.Abs(step) * _n / 2);
            }
            double err = crossPhase - _crossTarget;
            err -= Math.Floor(err + 0.5);
            bool data = _state == State.Data;
            _phase -= err * weight * (data ? _opt.DataGain : _opt.HuntGain);
            if (_opt.TrackFrequency)
                _freqOffset = Math.Clamp(_freqOffset - err * weight * (data ? _opt.DataRateGain : _opt.HuntRateGain), -0.06, 0.06);
        }
        _prevD = d;
        _dSum += d - _dRing[_dPos];
        _dRing[_dPos] = d;
        if (++_dPos == _dRing.Length) _dPos = 0;
        double decision = _dSum / _dRing.Length;

        if (_phase >= 1.0)
        {
            _phase -= 1.0;
            float soft = (float)Math.Clamp(decision, -1, 1);
            _lastBits[_lastBitPos] = soft;
            _lastBitPos = (_lastBitPos + 1) & 7;
            if (_state == State.Data) _soft.Add(soft);
            OnBit(decision >= 0 ? 1 : 0, Math.Min(1, Math.Abs(decision)));
        }
    }

    private void OnBit(int bit, double margin)
    {
        _shift = ((_shift >> 1) | (bit << 7)) & 0xFF;

        if (_state == State.Hunting)
        {
            if (_shift == PreambleByte && _quality > SyncQuality)
            {
                _state = State.Preamble;
                _bitCount = 0;
                _preambleBytes = 1;
                _burstStart = _sample - (long)(8 * _samplesPerBit);
                _syncedThisCarrier = true;
            }
            return;
        }

        if (_state == State.Data)
        {
            if (_quality < DataLostQuality)
            {
                if (++_lostBits > 4) { EndBurst(); return; }
            }
            else _lostBits = 0;
            _marginSum += margin;
            _marginCount++;
        }

        if (++_bitCount < 8) return;
        _bitCount = 0;
        int b = _shift;

        if (_state == State.Preamble)
        {
            bool tolerant = _opt.TolerantPreamble;
            if (b == PreambleByte || (tolerant && Distance(b, PreambleByte) == 1))
            {
                if (++_preambleBytes > 80) ResetFraming();
                return;
            }
            if (tolerant && PreambleRotation(b) is int slip)
            {
                _bitCount = slip;
                return;
            }
            bool nearStart = tolerant && _preambleBytes >= 3 && (Distance(b, 'Z') <= 2 || Distance(b, 'N') <= 2);
            if (b == 'Z' || b == 'N' || (_preambleBytes >= 3 && IsValidChar(b)) || nearStart)
            {
                _state = State.Data;
                _text.Clear();
                _raw.Clear();
                _soft.Clear();
                for (int i = 0; i < 8; i++) _soft.Add(_lastBits[(_lastBitPos + i) & 7]);
                _raw.Add((byte)b);
                _marginSum = 0; _marginCount = 0;
                _errorCount = 0; _consecutiveErrors = 0; _lostBits = 0;
                _plusIndex = -1; _dashesAfterPlus = 0;
                if (IsValidChar(b)) AppendChar((char)b);
                else { _errorCount = 1; _consecutiveErrors = 1; AppendChar('?'); }
            }
            else
            {
                if (_preambleBytes >= 2)
                    SyncFailed?.Invoke(new SameSyncFailure(
                        $"preamble found ({_preambleBytes}/16 bytes) but the next byte 0x{b:X2} does not start a header",
                        _preambleBytes, [(byte)b], _quality, _burstStart, _sample));
                ResetFraming();
            }
            return;
        }

        if (IsValidChar(b))
        {
            _consecutiveErrors = 0;
            _raw.Add((byte)b);
            AppendChar((char)b);
        }
        else
        {
            _errorCount++;
            if (++_consecutiveErrors >= 3) { EndBurst(); return; }
            _raw.Add((byte)b);
            AppendChar('?');
        }
    }

    private void AppendChar(char c)
    {
        _text.Append(c);
        int len = _text.Length;

        if (len == 4 && SameBurst.Classify(_text.ToString()) == SameBurstKind.EndOfMessage)
        {
            EndBurst();
            return;
        }

        if (c == '+' && _plusIndex < 0 && len > 4)
        {
            _plusIndex = len - 1;
        }
        else if (c == '-' && _plusIndex >= 0 && ++_dashesAfterPlus >= 3)
        {
            EndBurst();
            return;
        }

        if ((_plusIndex >= 0 && len - _plusIndex > 26) || len >= MaxBurstLength) EndBurst();
    }

    private void ReportCarrierWithoutSync()
    {
        double seconds = (_sample - _carrierStart) / (double)SampleRate;
        double meanQuality = _carrierQualityCount > 0 ? _carrierQualitySum / _carrierQualityCount : 0;
        if (!_syncedThisCarrier && seconds >= 0.3 && meanQuality >= 0.6)
            SyncFailed?.Invoke(new SameSyncFailure(
                $"SAME-like tones for {seconds:0.0} s but no preamble could be read",
                0, [], meanQuality, _carrierStart, _sample));
    }

    private void EndBurst()
    {
        string text = _text.ToString().TrimEnd('?');
        if (text.Length < 4 && _raw.Count > 0)
        {
            SyncFailed?.Invoke(new SameSyncFailure(
                $"lost the signal after {_raw.Count} byte{(_raw.Count == 1 ? "" : "s")}",
                _preambleBytes, _raw.ToArray(), _marginCount > 0 ? _marginSum / _marginCount : 0, _burstStart, _sample));
        }
        if (text.Length >= 4)
        {
            var raw = _raw.Take(text.Length).ToArray();
            var softBits = _soft.Take(text.Length * 8).ToArray();
            var kind = SameBurst.Classify(text);
            if (kind == SameBurstKind.Header && !text.StartsWith("ZCZC", StringComparison.Ordinal))
                text = "ZCZC" + text[4..];
            else if (kind == SameBurstKind.EndOfMessage)
                text = "NNNN";
            int damaged = raw.Count(b => !SameBurst.IsValidByte(b));
            BurstDecoded?.Invoke(new SameBurst(text, kind, _burstStart, _sample, damaged, _preambleBytes)
            {
                RawBytes = raw,
                SoftBits = softBits.Length == raw.Length * 8 ? softBits : [],
                Quality = _marginCount > 0 ? _marginSum / _marginCount : 0,
            });
        }
        ResetFraming();
    }

    private void ResetFraming()
    {
        _state = State.Hunting;
        _text.Clear();
        _shift = 0; _bitCount = 0; _preambleBytes = 0;
        _errorCount = 0; _consecutiveErrors = 0; _lostBits = 0;
        _plusIndex = -1; _dashesAfterPlus = 0;
    }

    private static bool IsValidChar(int b) => b >= 0x20 && b <= 0x7E;

    private static double MeasurePowerGain(int rate, double freq)
    {
        Dsp.Biquad[] f = [Dsp.Biquad.HighPass(rate, 900), Dsp.Biquad.HighPass(rate, 900), Dsp.Biquad.LowPass(rate, 3000), Dsp.Biquad.LowPass(rate, 3000)];
        double inP = 0, outP = 0;
        int n = rate / 2;
        for (int i = 0; i < n; i++)
        {
            float x = (float)Math.Sin(2 * Math.PI * freq * i / rate), y = x;
            foreach (var b in f) y = b.Process(y);
            if (i < n / 2) continue;
            inP += x * x;
            outP += y * y;
        }
        return outP / inP;
    }

    private static int Distance(int a, int b) => System.Numerics.BitOperations.PopCount((uint)((a ^ b) & 0xFF));

    private static int? PreambleRotation(int b)
    {
        for (int k = 1; k < 8; k++)
        {
            int rotated = ((PreambleByte >> k) | (PreambleByte << (8 - k))) & 0xFF;
            if (rotated == b) return k;
        }
        return null;
    }
}
