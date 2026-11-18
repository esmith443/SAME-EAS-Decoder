using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SameEasDecoder.Audio;

public enum AudioDeviceKind { Input, Loopback }

public sealed record AudioDevice(string Id, string Name, AudioDeviceKind Kind, bool IsDefault)
{
    public string Glyph => Kind == AudioDeviceKind.Input ? "" : "";
    public string KindLabel => Kind == AudioDeviceKind.Input ? "Input" : "System audio";
    public string GroupName => Kind == AudioDeviceKind.Input ? "RECORDING DEVICES" : "SYSTEM AUDIO (LOOPBACK)";
    public string Detail => (Kind == AudioDeviceKind.Input ? "Recording device" : "Loopback · what this PC is playing") + (IsDefault ? " · default" : "");
}

public static class AudioDevices
{
    public static List<AudioDevice> Enumerate()
    {
        var result = new List<AudioDevice>();
        using var enumerator = new MMDeviceEnumerator();
        string? defaultIn = DefaultId(enumerator, DataFlow.Capture);
        string? defaultOut = DefaultId(enumerator, DataFlow.Render);

        foreach (var flow in new[] { DataFlow.Capture, DataFlow.Render })
        {
            var kind = flow == DataFlow.Capture ? AudioDeviceKind.Input : AudioDeviceKind.Loopback;
            string? def = flow == DataFlow.Capture ? defaultIn : defaultOut;
            var items = new List<AudioDevice>();
            foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    items.Add(new AudioDevice(d.ID, d.FriendlyName, kind, d.ID == def));
                }
                catch (Exception)
                {
                }
                finally
                {
                    d.Dispose();
                }
            }
            result.AddRange(items.OrderByDescending(i => i.IsDefault).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase));
        }
        return result;
    }

    private static string? DefaultId(MMDeviceEnumerator e, DataFlow flow)
    {
        try
        {
            if (!e.HasDefaultAudioEndpoint(flow, Role.Multimedia)) return null;
            using var d = e.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return d.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class SampleConverter
{
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public static bool IsFloat(WaveFormat format) => format.Encoding switch
    {
        WaveFormatEncoding.IeeeFloat => true,
        WaveFormatEncoding.Extensible when format is WaveFormatExtensible ext => ext.SubFormat == FloatSubtype ||
            (ext.SubFormat != PcmSubtype && format.BitsPerSample == 32),
        WaveFormatEncoding.Extensible => format.BitsPerSample == 32,
        _ => false,
    };

    public static float[] ToFloat(byte[] buffer, int bytes, WaveFormat format)
    {
        int bits = format.BitsPerSample;
        if (IsFloat(format))
        {
            if (bits == 32)
            {
                var f = new float[bytes / 4];
                Buffer.BlockCopy(buffer, 0, f, 0, f.Length * 4);
                return f;
            }
            if (bits == 64)
            {
                var f = new float[bytes / 8];
                for (int i = 0; i < f.Length; i++) f[i] = (float)BitConverter.ToDouble(buffer, i * 8);
                return f;
            }
        }

        switch (bits)
        {
            case 16:
            {
                var f = new float[bytes / 2];
                for (int i = 0; i < f.Length; i++) f[i] = (short)(buffer[i * 2] | (buffer[i * 2 + 1] << 8)) / 32768f;
                return f;
            }
            case 24:
            {
                var f = new float[bytes / 3];
                for (int i = 0; i < f.Length; i++)
                {
                    int o = i * 3;
                    int v = buffer[o] | (buffer[o + 1] << 8) | ((sbyte)buffer[o + 2] << 16);
                    f[i] = v / 8388608f;
                }
                return f;
            }
            case 32:
            {
                var f = new float[bytes / 4];
                for (int i = 0; i < f.Length; i++) f[i] = BitConverter.ToInt32(buffer, i * 4) / 2147483648f;
                return f;
            }
            case 8:
            {
                var f = new float[bytes];
                for (int i = 0; i < f.Length; i++) f[i] = (buffer[i] - 128) / 128f;
                return f;
            }
        }
        throw new NotSupportedException($"Unsupported audio format: {format}");
    }
}
