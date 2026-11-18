using System.Text;

namespace SameEasDecoder.Core.Audio;

public static class WavFile
{
    public static void WriteMono16(string path, ReadOnlySpan<float> samples, int sampleRate)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs, Encoding.ASCII);
        int dataBytes = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(sampleRate); w.Write(sampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (float s in samples) w.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
    }

    public static (float[] Samples, int SampleRate, int Channels) Read(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        r.ReadInt32();
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        int format = 1, channels = 1, rate = 0, bits = 16;
        while (r.BaseStream.Position < r.BaseStream.Length)
        {
            string id = Encoding.ASCII.GetString(r.ReadBytes(4));
            int size = r.ReadInt32();
            if (id == "fmt ")
            {
                format = r.ReadInt16(); channels = r.ReadInt16(); rate = r.ReadInt32();
                r.ReadInt32(); r.ReadInt16(); bits = r.ReadInt16();
                r.BaseStream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                byte[] data = r.ReadBytes(size);
                float[] samples;
                if (format == 3 && bits == 32)
                {
                    samples = new float[data.Length / 4];
                    Buffer.BlockCopy(data, 0, samples, 0, samples.Length * 4);
                }
                else if (bits == 16)
                {
                    samples = new float[data.Length / 2];
                    for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
                }
                else throw new NotSupportedException($"Unsupported WAV format {format}/{bits}-bit.");
                return (samples, rate, channels);
            }
            else r.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
        }
        throw new InvalidDataException("No data chunk.");
    }
}
