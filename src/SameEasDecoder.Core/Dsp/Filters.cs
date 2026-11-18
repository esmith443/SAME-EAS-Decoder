namespace SameEasDecoder.Core.Dsp;

public static class Filters
{
    public static float[] DesignLowPass(int taps, double cutoff)
    {
        var h = new double[taps];
        int m = taps - 1;
        double sum = 0;
        for (int i = 0; i < taps; i++)
        {
            double x = i - m / 2.0;
            double sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            double w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / m) + 0.08 * Math.Cos(4 * Math.PI * i / m);
            h[i] = sinc * w;
            sum += h[i];
        }
        var result = new float[taps];
        for (int i = 0; i < taps; i++) result[i] = (float)(h[i] / sum);
        return result;
    }
}

public sealed class Biquad
{
    private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    private Biquad() { }

    public static Biquad HighPass(double sampleRate, double frequency, double q = 0.7071)
    {
        double w0 = 2 * Math.PI * frequency / sampleRate, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha;
        return new Biquad
        {
            _b0 = (1 + cos) / 2 / a0, _b1 = -(1 + cos) / a0, _b2 = (1 + cos) / 2 / a0,
            _a1 = -2 * cos / a0, _a2 = (1 - alpha) / a0,
        };
    }

    public static Biquad LowPass(double sampleRate, double frequency, double q = 0.7071)
    {
        double w0 = 2 * Math.PI * frequency / sampleRate, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha;
        return new Biquad
        {
            _b0 = (1 - cos) / 2 / a0, _b1 = (1 - cos) / a0, _b2 = (1 - cos) / 2 / a0,
            _a1 = -2 * cos / a0, _a2 = (1 - alpha) / a0,
        };
    }

    public float Process(float x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return (float)y;
    }

    public void Reset() => _z1 = _z2 = 0;
}
