using SameEasDecoder.Core;
using SameEasDecoder.Core.Same;

namespace SameEasDecoder.Tests;

public class SameDecoderTests
{
    private const string TorHeader = "ZCZC-WXR-TOR-026161-026093-126125+0045-3211704-KDTX/NWS-";
    private const string RwtHeader = "ZCZC-EAS-RWT-000000+0015-3220930-WXYZ/FM -";

    internal static float[] AddNoise(float[] signal, double noiseRms, int seed = 1)
    {
        var rng = new Random(seed);
        var result = new float[signal.Length];
        for (int i = 0; i < signal.Length; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double g = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
            result[i] = (float)(signal[i] + g * noiseRms);
        }
        return result;
    }

    internal static float[] Resample(float[] input, int fromRate, int toRate)
    {
        var r = new SameEasDecoder.Core.Dsp.StreamingResampler(fromRate, toRate);
        var output = new List<float>();
        r.Process(input, output);
        r.Process(new float[fromRate], output);
        return output.ToArray();
    }

    internal static float[] Interleave(float[] mono, int channels, bool silentRight = false)
    {
        var result = new float[mono.Length * channels];
        for (int i = 0; i < mono.Length; i++)
            for (int c = 0; c < channels; c++)
                result[i * channels + c] = silentRight && c == 1 ? 0 : mono[i];
        return result;
    }

    private sealed class Capture
    {
        public List<SameAlert> Alerts { get; } = [];
        public List<SameEndOfMessage> Eoms { get; } = [];
        public List<SameBurst> Bursts { get; } = [];
        public List<string> Rejected { get; } = [];
        public List<(AttentionToneKind Kind, TimeSpan Duration)> Tones { get; } = [];
    }

    private static Capture Run(float[] interleaved, int sampleRate, int channels, ChannelMode mode = ChannelMode.Mix)
    {
        var cap = new Capture();
        using var p = new DecoderPipeline { ChannelMode = mode };
        p.SameAlertDecoded += a => cap.Alerts.Add(a);
        p.SameEndOfMessage += e => cap.Eoms.Add(e);
        p.SameBurstDecoded += b => cap.Bursts.Add(b);
        p.SameHeaderRejected += (t, _) => cap.Rejected.Add(t);
        p.AttentionToneEnded += (k, d) => cap.Tones.Add((k, d));
        p.Start(sampleRate, channels);
        int block = sampleRate / 100 * channels;
        for (int i = 0; i < interleaved.Length; i += block)
            p.Process(interleaved.AsSpan(i, Math.Min(block, interleaved.Length - i)));
        p.ProcessSilence(TimeSpan.FromSeconds(4));
        p.Stop();
        return cap;
    }

    [Fact]
    public void Decodes_clean_activation_at_16kHz()
    {
        var audio = SameEncoder.EncodeMessage(TorHeader, 16000, attentionSeconds: 8);
        var cap = Run(audio, 16000, 1);

        var alert = Assert.Single(cap.Alerts);
        Assert.Equal(TorHeader, alert.Header.Raw);
        Assert.Equal(3, alert.Vote.BurstCount);
        Assert.Equal(3, alert.Vote.ExactMatches);
        Assert.True(alert.Verified);
        Assert.Single(cap.Eoms);
        var tone = Assert.Single(cap.Tones);
        Assert.Equal(AttentionToneKind.EasDualTone, tone.Kind);
        Assert.InRange(tone.Duration.TotalSeconds, 7.0, 9.0);
    }

    [Theory]
    [InlineData(48000, 2)]
    [InlineData(44100, 2)]
    [InlineData(22050, 1)]
    [InlineData(96000, 2)]
    [InlineData(8000, 1)]
    public void Decodes_at_common_device_rates(int rate, int channels)
    {
        var audio = SameEncoder.EncodeMessage(RwtHeader, rate, attentionSeconds: 0, messageGapSeconds: 1, amplitude: 0.3f);
        var cap = Run(Interleave(audio, channels), rate, channels);

        var alert = Assert.Single(cap.Alerts);
        Assert.Equal(RwtHeader, alert.Header.Raw);
        Assert.True(alert.Verified);
        Assert.Single(cap.Eoms);
    }

    [Theory]
    [InlineData(0.05, 3)]
    [InlineData(0.10, 7)]
    [InlineData(0.15, 11)]
    public void Decodes_in_noise(double noiseRms, int seed)
    {
        var audio = SameEncoder.EncodeMessage(TorHeader, 48000, attentionSeconds: 0, messageGapSeconds: 1, amplitude: 0.25f);
        var noisy = AddNoise(audio, noiseRms, seed);
        var cap = Run(noisy, 48000, 1);

        var alert = Assert.Single(cap.Alerts);
        Assert.Equal(TorHeader, alert.Header.Raw);
    }

    [Fact]
    public void Low_level_signal_decodes()
    {
        var audio = SameEncoder.EncodeMessage(RwtHeader, 44100, attentionSeconds: 0, messageGapSeconds: 1, amplitude: 0.003f);
        var cap = Run(AddNoise(audio, 0.0003), 44100, 1);
        Assert.Equal(RwtHeader, Assert.Single(cap.Alerts).Header.Raw);
    }

    [Fact]
    public void Right_channel_selection_uses_only_that_channel()
    {
        var audio = SameEncoder.EncodeMessage(RwtHeader, 48000, attentionSeconds: 0, messageGapSeconds: 1);
        var stereo = Interleave(audio, 2, silentRight: true);
        Assert.Empty(Run(stereo, 48000, 2, ChannelMode.Right).Alerts);
        Assert.Single(Run(stereo, 48000, 2, ChannelMode.Left).Alerts);
    }

    [Fact]
    public void White_noise_produces_no_alerts()
    {
        var noise = AddNoise(new float[16000 * 60], 0.2, seed: 42);
        var cap = Run(noise, 16000, 1);
        Assert.Empty(cap.Alerts);
        Assert.Empty(cap.Eoms);
        Assert.Empty(cap.Tones);
    }

    [Fact]
    public void Nwr_tone_is_detected()
    {
        var tone = SameEncoder.Tone(16000, 10, 0.4f, 1050);
        var cap = Run(AddNoise(tone, 0.02), 16000, 1);
        var t = Assert.Single(cap.Tones);
        Assert.Equal(AttentionToneKind.NwrTone, t.Kind);
        Assert.InRange(t.Duration.TotalSeconds, 9.0, 11.0);
    }

    [Fact]
    public void Records_activation_audio_from_header_to_eom()
    {
        string dir = Path.Combine(Path.GetTempPath(), "same-eas-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var audio = SameEncoder.EncodeMessage(TorHeader, 48000, attentionSeconds: 8, nwrTone: true, messageGapSeconds: 5);
            var lead = new float[48000 * 10];
            var input = lead.Concat(audio).Concat(new float[48000 * 10]).ToArray();

            var saved = new List<AlertRecording>();
            using var done = new ManualResetEventSlim();
            using var p = new DecoderPipeline { RecordingEnabled = true, RecordingDirectory = dir };
            p.RecordingSaved += r => { saved.Add(r); done.Set(); };
            p.Start(48000, 1);
            for (int i = 0; i < input.Length; i += 480) p.Process(input.AsSpan(i, Math.Min(480, input.Length - i)));
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            p.Stop();

            var rec = Assert.Single(saved);
            Assert.True(File.Exists(rec.Path));
            Assert.Equal("TOR", rec.Alert?.Header.EventCode);
            Assert.Contains("TOR WXR", Path.GetFileName(rec.Path));
            double expected = audio.Length / 48000.0;
            Assert.InRange(rec.Duration.TotalSeconds, expected - 1, expected + 7);
            var (samples, rate, _) = SameEasDecoder.Core.Audio.WavFile.Read(rec.Path);
            Assert.Equal(16000, rate);
            Assert.Equal(rec.Duration.TotalSeconds, samples.Length / 16000.0, 2);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Bursts_report_raw_bytes_quality_and_damage()
    {
        const int rate = 16000;
        string damaged = TorHeader[..13] + '²' + TorHeader[14..];
        var gap = new float[rate];
        var audio = new float[rate / 2]
            .Concat(SameEncoder.EncodeBurst(TorHeader, rate)).Concat(gap)
            .Concat(SameEncoder.EncodeBurst(damaged, rate)).Concat(gap)
            .Concat(SameEncoder.EncodeBurst(TorHeader, rate)).Concat(gap).ToArray();
        var cap = Run(audio, rate, 1);

        Assert.Equal(3, cap.Bursts.Count);
        var clean = cap.Bursts[0];
        Assert.Equal(TorHeader, System.Text.Encoding.ASCII.GetString(clean.RawBytes));
        Assert.Empty(clean.DamagedPositions);
        Assert.True(clean.Quality > 0.9, $"quality {clean.Quality:F2}");

        var bad = cap.Bursts[1];
        Assert.Equal([13], bad.DamagedPositions);
        Assert.Equal(0xB2, bad.RawBytes[13]);
        Assert.Equal('?', bad.Text[13]);
        Assert.Equal(1, bad.ErrorCount);

        var alert = Assert.Single(cap.Alerts);
        Assert.Equal(TorHeader, alert.Header.Raw);
        Assert.Equal(2, alert.Vote.ExactMatches);
    }

    [Fact]
    public void Provisional_alerts_arrive_after_first_and_second_burst()
    {
        const int rate = 16000;
        string wrongEvent = TorHeader.Replace("-TOR-", "-TOA-");
        var gap = new float[rate];
        var audio = new float[rate / 2]
            .Concat(SameEncoder.EncodeBurst(wrongEvent, rate)).Concat(gap)
            .Concat(SameEncoder.EncodeBurst(TorHeader, rate)).Concat(gap)
            .Concat(SameEncoder.EncodeBurst(TorHeader, rate)).Concat(gap).ToArray();

        var progress = new List<SameAlert>();
        var finals = new List<SameAlert>();
        using var p = new DecoderPipeline();
        p.SameAlertProgress += progress.Add;
        p.SameAlertDecoded += finals.Add;
        p.Start(rate, 1);
        for (int i = 0; i < audio.Length; i += 160) p.Process(audio.AsSpan(i, Math.Min(160, audio.Length - i)));
        p.Stop();

        Assert.Equal(2, progress.Count);
        Assert.Equal("TOA", progress[0].Header.EventCode);
        Assert.Equal(1, progress[0].Vote.BurstCount);
        Assert.Equal(2, progress[1].Vote.BurstCount);
        var final = Assert.Single(finals);
        Assert.Equal("TOR", final.Header.EventCode);
        Assert.Equal(2, final.Vote.ExactMatches);
    }

    [Fact]
    public void Preamble_with_bit_errors_still_syncs()
    {
        const int rate = 16000;
        var bytes = Enumerable.Repeat((byte)0xAB, 16).ToArray();
        bytes[3] = 0xAA; bytes[7] = 0xAA; bytes[12] = 0xAA; bytes[15] = 0xAA;
        string text = new string(bytes.Select(b => (char)b).ToArray()) + RwtHeader;
        var audio = new float[rate / 2].Concat(EncodeRaw(text, rate)).Concat(new float[rate]).ToArray();

        var bursts = new List<SameBurst>();
        var demod = new SameDemodulator(rate);
        demod.BurstDecoded += bursts.Add;
        demod.Process(audio);
        Assert.Equal(RwtHeader, Assert.Single(bursts).Text);

        var legacy = new List<SameBurst>();
        var old = new SameDemodulator(rate, SameDemodulatorOptions.Legacy);
        old.BurstDecoded += legacy.Add;
        old.Process(audio);
        Assert.True(legacy.Count == 0 || legacy[0].PreambleBytes < 4);
    }

    [Fact]
    public void Tracks_a_sender_with_off_rate_bit_clock()
    {
        const int rate = 16000;
        var fast = EncodeRaw(new string((char)0xAB, 16) + TorHeader, rate, baudScale: 1.015);
        var audio = new float[rate / 2].Concat(fast).Concat(new float[rate]).ToArray();
        var bursts = new List<SameBurst>();
        var demod = new SameDemodulator(rate);
        demod.BurstDecoded += bursts.Add;
        demod.Process(audio);
        Assert.Equal(TorHeader, Assert.Single(bursts).Text);
        Assert.InRange(demod.ClockOffset, 0.005, 0.025);
    }

    [Fact]
    public void Soft_combining_rebuilds_a_header_damaged_in_every_copy()
    {
        static SameBurst Copy(string text, params (int Bit, float Confidence)[] marks)
        {
            var truth = text.SelectMany(c => Enumerable.Range(0, 8).Select(i => ((c >> i) & 1) == 1 ? 1f : -1f)).ToArray();
            var soft = truth.Select(t => t * 0.9f).ToArray();
            foreach (var (bit, conf) in marks) soft[bit] = truth[bit] * conf;
            var raw = new byte[text.Length];
            for (int i = 0; i < soft.Length; i++) if (soft[i] >= 0) raw[i / 8] |= (byte)(1 << (i % 8));
            string read = new(raw.Select(b => SameBurst.IsValidByte(b) ? (char)b : '?').ToArray());
            return new SameBurst(read, SameBurstKind.Header, 0, 0, 0, 16) { RawBytes = raw, SoftBits = soft };
        }
        static int Bit(int ch, int bit) => ch * 8 + bit;
        var a = Copy(TorHeader, (Bit(20, 0), -0.2f), (Bit(33, 1), -0.9f));
        var b = Copy(TorHeader, (Bit(26, 2), -0.9f), (Bit(41, 3), -0.6f));
        var c = Copy(TorHeader, (Bit(20, 0), 0.1f), (Bit(48, 0), -0.95f));
        Assert.All(new[] { a, b, c }, x => Assert.NotEqual(TorHeader, x.Text));

        var vote = SameVoting.Vote([a, b, c]);
        Assert.True(vote.SoftCombined);
        Assert.Equal(TorHeader, vote.Text);
    }

    [Theory]
    [InlineData("ZCZC-EAS)RMT-005000+0100-1331450-KSAR    -", "ZCZC-EAS-RMT-005000+0100-1331450-KSAR    -")]
    [InlineData("ZCZC-WXR-TOR-02616!-026093+0045-3211704-KDTX/NWS-", "ZCZC-WXR-TOR-026161-026093+0045-3211704-KDTX/NWS-")]
    [InlineData("ZCZC-WXR-TOR-026161-026093#0045-3211704-KDTX/NWS-", "ZCZC-WXR-TOR-026161-026093+0045-3211704-KDTX/NWS-")]
    public void Repairs_characters_that_break_the_header_layout(string damaged, string expected)
    {
        Assert.True(SameHeaderRepair.TryRepair(damaged, out var fixedText, out int changes));
        Assert.Equal(expected, fixedText);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Repair_refuses_far_or_ambiguous_fixes()
    {
        Assert.False(SameHeaderRepair.TryRepair("ZCZC-WXR-TOR-0261Z1+0045-3211704-KDTX/NWS-", out _, out _));
        Assert.False(SameHeaderRepair.TryRepair("ZCZC-WXR-TOR-0261:1+0045-3211704-KDTX/NWS-", out _, out _));
    }

    private static float[] EncodeRaw(string bytes, int rate, double baudScale = 1.0)
    {
        double spb = rate / (SameDemodulator.BaudRate * baudScale);
        int total = (int)Math.Ceiling(bytes.Length * 8 * spb);
        var output = new float[total];
        double phase = 0;
        int sample = 0;
        for (int i = 0; i < bytes.Length * 8; i++)
        {
            int bit = ((byte)bytes[i / 8] >> (i % 8)) & 1;
            double inc = 2 * Math.PI * (bit == 1 ? SameDemodulator.MarkFrequency : SameDemodulator.SpaceFrequency) / rate;
            int end = Math.Min(total, (int)Math.Round((i + 1) * spb));
            for (; sample < end; sample++) { output[sample] = (float)(0.5 * Math.Sin(phase)); phase += inc; }
        }
        return output;
    }

    [Fact]
    public void Voting_recovers_from_one_corrupted_burst()
    {
        var vote = SameVoting.Vote([TorHeader, TorHeader.Replace("TOR", "T?R"), TorHeader]);
        Assert.Equal(TorHeader, vote.Text);
        Assert.Equal(2, vote.ExactMatches);

        string a = "ZCZC-WXR-T?R-026161-026093-126125+0045-3211704-KDTX/NWS-";
        string b = "ZCZC-WXR-TOR-02?161-026093-126125+0045-3211704-KDTX/NWS-";
        string c = "ZCZC-WXR-TOR-026161-026093-126125+0045-321170?-KDTX/NWS-";
        var v2 = SameVoting.Vote([a, b, c]);
        Assert.Equal(TorHeader, v2.Text);
        Assert.True(v2.CharacterVote);
    }

    [Fact]
    public void Parses_header_fields()
    {
        var reference = new DateTime(2026, 11, 17, 18, 0, 0, DateTimeKind.Utc);
        Assert.True(SameHeader.TryParse(TorHeader, reference, out var h));
        Assert.NotNull(h);
        Assert.Equal("WXR", h.OriginatorCode);
        Assert.Equal("National Weather Service", h.Originator);
        Assert.Equal("Tornado Warning", h.Event.Name);
        Assert.Equal(SameSeverity.Warning, h.Event.Severity);
        Assert.Equal(3, h.Locations.Count);
        Assert.Equal(26, h.Locations[0].StateFips);
        Assert.Equal(161, h.Locations[0].CountyFips);
        Assert.Equal(1, h.Locations[2].Subdivision);
        Assert.StartsWith("Northwest", h.Locations[2].Description);
        Assert.Equal(TimeSpan.FromMinutes(45), h.ValidFor);
        Assert.Equal(new DateTime(2026, 11, 17, 17, 4, 0, DateTimeKind.Utc), h.IssuedUtc);
        Assert.Equal("KDTX/NWS", h.StationId);
    }

    [Fact]
    public void Issue_time_rolls_back_a_year_across_new_year()
    {
        var reference = new DateTime(2027, 1, 1, 0, 30, 0, DateTimeKind.Utc);
        Assert.True(SameHeader.TryParse("ZCZC-WXR-WSW-036029+0600-3652330-KBUF/NWS-", reference, out var h));
        Assert.Equal(new DateTime(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc), h!.IssuedUtc);
    }

    [Fact]
    public void Statewide_and_marine_locations_are_described()
    {
        Assert.True(SameHeader.TryParse("ZCZC-PEP-EAN-000000-048000-073000+0100-3211704-FEMA    -", DateTime.UtcNow, out var h));
        Assert.Equal("Entire United States", h!.Locations[0].Description);
        Assert.Equal("All of Texas", h.Locations[1].Description);
        Assert.Contains("Marine zone", h.Locations[2].Description);
        Assert.Equal(SameSeverity.Emergency, h.Event.Severity);
    }

    [Theory]
    [InlineData("ZCZC-WXR-TOR-026161+0045-3211704-KDTX/NWS", true)]
    [InlineData("ZCZC-WXR-TOR-02616+0045-3211704-KDTX/NWS-", false)]
    [InlineData("ZCZC-WXR-TOR-026161+0045-3999704-KDTX/NWS-", false)]
    [InlineData("ZCZD-WXR-TOR-026161+0045-3211704-KDTX/NWS-", false)]
    [InlineData("ZCZC-WXR-TOR-026161+0075-3211704-KDTX/NWS-", false)]
    public void Validates_header_format(string text, bool expected)
    {
        Assert.Equal(expected, SameHeader.TryParse(text, DateTime.UtcNow, out _));
    }
}
