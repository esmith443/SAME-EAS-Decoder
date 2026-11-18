using SameEasDecoder.Core.Same;

namespace SameEasDecoder.Tests;

internal static class SameEncoder
{
    public static float[] EncodeBurst(string text, int sampleRate, float amplitude = 0.5f, double phase = 0)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < 16; i++) bytes.Add(SameDemodulator.PreambleByte);
        foreach (char c in text) bytes.Add((byte)c);

        int totalBits = bytes.Count * 8;
        int totalSamples = (int)Math.Ceiling(totalBits * sampleRate / SameDemodulator.BaudRate);
        var output = new float[totalSamples];
        double samplesPerBit = sampleRate / SameDemodulator.BaudRate;

        int sample = 0;
        for (int bitIndex = 0; bitIndex < totalBits; bitIndex++)
        {
            int bit = (bytes[bitIndex / 8] >> (bitIndex % 8)) & 1;
            double freq = bit == 1 ? SameDemodulator.MarkFrequency : SameDemodulator.SpaceFrequency;
            double inc = 2 * Math.PI * freq / sampleRate;
            int end = Math.Min(totalSamples, (int)Math.Round((bitIndex + 1) * samplesPerBit));
            for (; sample < end; sample++)
            {
                output[sample] = (float)(amplitude * Math.Sin(phase));
                phase += inc;
            }
        }
        return output;
    }

    public static float[] Tone(int sampleRate, double seconds, float amplitude, params double[] frequencies)
    {
        int n = (int)(sampleRate * seconds);
        var output = new float[n];
        float a = amplitude / frequencies.Length;
        foreach (double f in frequencies)
            for (int i = 0; i < n; i++)
                output[i] += (float)(a * Math.Sin(2 * Math.PI * f * i / sampleRate));
        return output;
    }

    public static float[] EncodeMessage(string header, int sampleRate, double attentionSeconds = 8,
        bool nwrTone = false, double messageGapSeconds = 2, float amplitude = 0.5f)
    {
        var parts = new List<float[]>();
        var silence = new float[sampleRate];
        parts.Add(new float[sampleRate / 2]);
        for (int i = 0; i < 3; i++) { parts.Add(EncodeBurst(header, sampleRate, amplitude)); parts.Add(silence); }
        if (attentionSeconds > 0)
        {
            parts.Add(nwrTone
                ? Tone(sampleRate, attentionSeconds, amplitude, 1050)
                : Tone(sampleRate, attentionSeconds, amplitude, 853, 960));
            parts.Add(silence);
        }
        if (messageGapSeconds > 0) parts.Add(new float[(int)(sampleRate * messageGapSeconds)]);
        for (int i = 0; i < 3; i++) { parts.Add(EncodeBurst("NNNN", sampleRate, amplitude)); parts.Add(silence); }

        var result = new float[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (var p in parts) { p.CopyTo(result, offset); offset += p.Length; }
        return result;
    }
}
