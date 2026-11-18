namespace SameEasDecoder.Core.Dsp;

public sealed class StreamingResampler
{
    private readonly double _step;
    private readonly float[] _taps;
    private readonly int _half;
    private readonly bool _passthrough;
    private float[] _buf;
    private int _count;
    private double _pos;

    public StreamingResampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));

        InputRate = inputRate;
        OutputRate = outputRate;
        _passthrough = inputRate == outputRate;
        _step = (double)inputRate / outputRate;

        double band = Math.Min(inputRate, outputRate);
        double cutoffHz = band * 0.42;
        double transitionHz = band * 0.15;
        int n = (int)Math.Ceiling(5.5 * inputRate / transitionHz);
        if (n % 2 == 0) n++;
        n = Math.Clamp(n, 15, 801);

        _taps = Filters.DesignLowPass(n, cutoffHz / inputRate);
        _half = n / 2;
        _buf = new float[Math.Max(8192, n * 4)];
        _count = _half;
        _pos = _half;
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        if (_passthrough)
        {
            foreach (float s in input) output.Add(s);
            return;
        }

        EnsureCapacity(_count + input.Length);
        input.CopyTo(_buf.AsSpan(_count));
        _count += input.Length;

        while (true)
        {
            int i0 = (int)_pos;
            if (i0 + 1 + _half >= _count) break;
            double frac = _pos - i0;
            float y0 = Fir(i0);
            float y1 = frac > 1e-9 ? Fir(i0 + 1) : y0;
            output.Add((float)(y0 + (y1 - y0) * frac));
            _pos += _step;
        }

        int keepFrom = Math.Min((int)_pos - _half, _count);
        if (keepFrom > 0)
        {
            Array.Copy(_buf, keepFrom, _buf, 0, _count - keepFrom);
            _count -= keepFrom;
            _pos -= keepFrom;
        }
    }

    private float Fir(int center)
    {
        int start = center - _half;
        float acc = 0f;
        float[] t = _taps;
        float[] b = _buf;
        for (int k = 0; k < t.Length; k++) acc += b[start + k] * t[k];
        return acc;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length) return;
        int size = _buf.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _buf, size);
    }
}
