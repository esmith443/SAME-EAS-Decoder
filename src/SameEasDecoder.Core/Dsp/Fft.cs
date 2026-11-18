namespace SameEasDecoder.Core.Dsp;

public static class Fft
{
    public static void Transform(double[] re, double[] im)
    {
        int n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.");

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
            int halfLen = len >> 1;
            for (int i = 0; i < n; i += len)
            {
                double cRe = 1, cIm = 0;
                for (int k = 0; k < halfLen; k++)
                {
                    int a = i + k, b = a + halfLen;
                    double tRe = re[b] * cRe - im[b] * cIm;
                    double tIm = re[b] * cIm + im[b] * cRe;
                    re[b] = re[a] - tRe; im[b] = im[a] - tIm;
                    re[a] += tRe; im[a] += tIm;
                    double nRe = cRe * wRe - cIm * wIm;
                    cIm = cRe * wIm + cIm * wRe;
                    cRe = nRe;
                }
            }
        }
    }
}

public sealed class SpectrumAnalyzer
{
    private readonly float[] _buffer;
    private readonly double[] _window, _re, _im;
    private readonly int _hop;
    private readonly double _scale;
    private int _fill;

    public SpectrumAnalyzer(int size = 1024, int hop = 512)
    {
        Size = size;
        _hop = hop;
        _buffer = new float[size];
        _window = new double[size];
        _re = new double[size];
        _im = new double[size];
        double wsum = 0;
        for (int i = 0; i < size; i++)
        {
            _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1));
            wsum += _window[i];
        }
        _scale = 2.0 / wsum;
    }

    public int Size { get; }

    public event Action<float[]>? FrameReady;

    public void Reset() => _fill = 0;

    public void Process(ReadOnlySpan<float> samples)
    {
        int i = 0;
        while (i < samples.Length)
        {
            int take = Math.Min(Size - _fill, samples.Length - i);
            samples.Slice(i, take).CopyTo(_buffer.AsSpan(_fill));
            _fill += take;
            i += take;
            if (_fill == Size)
            {
                Emit();
                Array.Copy(_buffer, _hop, _buffer, 0, Size - _hop);
                _fill = Size - _hop;
            }
        }
    }

    private void Emit()
    {
        for (int i = 0; i < Size; i++)
        {
            _re[i] = _buffer[i] * _window[i];
            _im[i] = 0;
        }
        Fft.Transform(_re, _im);
        var db = new float[Size / 2];
        for (int i = 0; i < db.Length; i++)
        {
            double mag = Math.Sqrt(_re[i] * _re[i] + _im[i] * _im[i]) * _scale;
            db[i] = (float)(20 * Math.Log10(mag + 1e-9));
        }
        FrameReady?.Invoke(db);
    }
}
