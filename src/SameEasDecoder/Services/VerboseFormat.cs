using System.Globalization;
using SameEasDecoder.Core.Same;
using SameEasDecoder.ViewModels;

namespace SameEasDecoder.Services;

public static class VerboseFormat
{
    private const int MaxDamageLines = 8;

    public static string BurstName(SameBurstKind kind) => kind switch
    {
        SameBurstKind.Header => "Header burst",
        SameBurstKind.EndOfMessage => "End-of-message burst",
        _ => "Unrecognized burst",
    };

    public static string BurstSummary(SameBurst b, int number)
    {
        int damaged = b.ErrorCount;
        string state = damaged == 0 ? "clean" : damaged == 1 ? "1 damaged byte" : $"{damaged} damaged bytes";
        return $"{BurstName(b.Kind)} {number} · {b.RawBytes.Length} bytes · preamble {b.PreambleBytes}/16 · quality {b.Quality:P0} · {state}";
    }

    public static List<IReadOnlyList<LogSegment>> BurstLines(SameBurst b)
    {
        var lines = new List<IReadOnlyList<LogSegment>>();
        var received = new List<LogSegment> { new("rx    ", SegmentStyle.Dim) };
        AppendRaw(received, b.RawBytes);
        lines.Add(received);

        var damaged = b.DamagedPositions;
        foreach (int pos in damaged.Take(MaxDamageLines))
        {
            byte v = b.RawBytes[pos];
            string bits = Convert.ToString(v, 2).PadLeft(8, '0');
            lines.Add(
            [
                new($"byte {pos + 1,3}  ", SegmentStyle.Dim),
                new($"0x{v:X2} {bits[..4]} {bits[4..]}", SegmentStyle.Error),
                new($"  not a valid SAME character{Expected(b, pos)}", SegmentStyle.Dim),
            ]);
        }
        if (damaged.Count > MaxDamageLines)
            lines.Add([new($"… {damaged.Count - MaxDamageLines} more damaged bytes", SegmentStyle.Dim)]);
        return lines;
    }

    private static string Expected(SameBurst b, int pos)
    {
        if (b.Kind == SameBurstKind.EndOfMessage && pos < 4) return " (expected 'N')";
        if (b.Kind == SameBurstKind.Header && pos < 4) return $" (expected '{"ZCZC"[pos]}')";
        return "";
    }

    private static void AppendRaw(List<LogSegment> segs, byte[] raw)
    {
        var run = new System.Text.StringBuilder();
        foreach (byte v in raw)
        {
            if (SameBurst.IsValidByte(v)) { run.Append((char)v); continue; }
            if (run.Length > 0) { segs.Add(new(run.ToString())); run.Clear(); }
            segs.Add(new("?", SegmentStyle.Error));
        }
        if (run.Length > 0) segs.Add(new(run.ToString()));
    }

    public static string VoteSummary(SameVoteResult vote, bool accepted)
    {
        string how = vote switch
        {
            { BurstCount: 1 } => "only 1 burst received",
            { ExactMatches: >= 2 } v => $"{v.ExactMatches} of {v.BurstCount} identical",
            { SoftCombined: true } v => $"no two of {v.BurstCount} identical · rebuilt bit by bit from all copies",
            { CharacterVote: true } v => $"no two of {v.BurstCount} identical · rebuilt character by character",
            var v => $"{v.BurstCount} bursts disagree",
        };
        string repaired = vote.RepairedCharacters > 0
            ? $" · {vote.RepairedCharacters} character{(vote.RepairedCharacters == 1 ? "" : "s")} repaired from the SAME layout"
            : "";
        return $"Vote: {how}{repaired} · {(accepted ? "header accepted" : "header rejected")}";
    }

    public static List<IReadOnlyList<LogSegment>> SyncFailureLines(SameSyncFailure f)
    {
        var lines = new List<IReadOnlyList<LogSegment>>();
        if (f.RawBytes.Length > 0)
        {
            var rx = new List<LogSegment> { new("rx    ", SegmentStyle.Dim) };
            AppendRaw(rx, f.RawBytes);
            rx.Add(new($"   ({string.Join(" ", f.RawBytes.Select(b => $"0x{b:X2}"))})", SegmentStyle.Dim));
            lines.Add(rx);
        }
        lines.Add([new("preamble ", SegmentStyle.Dim), new($"{f.PreambleBytes}/16", SegmentStyle.Code),
                   new("   quality ", SegmentStyle.Dim), new($"{f.Quality:P0}", SegmentStyle.Code)]);
        return lines;
    }

    public static List<IReadOnlyList<LogSegment>> VoteLines(string voted, IReadOnlyList<string> bursts)
    {
        var lines = new List<IReadOnlyList<LogSegment>>();
        lines.Add([new("vote  ", SegmentStyle.Dim), new(voted, SegmentStyle.Good)]);
        for (int i = 0; i < bursts.Count; i++)
        {
            string t = bursts[i];
            var segs = new List<LogSegment> { new($"#{i + 1}    ", SegmentStyle.Dim) };
            int diffs = 0;
            var run = new System.Text.StringBuilder();
            int len = Math.Max(t.Length, voted.Length);
            for (int k = 0; k < len; k++)
            {
                char c = k < t.Length ? t[k] : '·';
                bool same = k < t.Length && k < voted.Length && t[k] == voted[k];
                if (same) { run.Append(c); continue; }
                diffs++;
                if (run.Length > 0) { segs.Add(new(run.ToString())); run.Clear(); }
                segs.Add(new(c.ToString(), c == '?' ? SegmentStyle.Error : SegmentStyle.Mismatch));
            }
            if (run.Length > 0) segs.Add(new(run.ToString()));
            segs.Add(diffs == 0
                ? new LogSegment("  ✓ matches", SegmentStyle.Good)
                : new LogSegment($"  {diffs} difference{(diffs == 1 ? "" : "s")}", SegmentStyle.Mismatch));
            lines.Add(segs);
        }
        return lines;
    }

    public static List<IReadOnlyList<LogSegment>> HeaderChangeLines(SameHeader before, SameHeader after)
    {
        var lines = new List<IReadOnlyList<LogSegment>>();
        void Change(string field, string was, string now, string meaning = "") =>
            lines.Add(
            [
                new(field.PadRight(9), SegmentStyle.Dim),
                new(was, SegmentStyle.Mismatch),
                new("  →  ", SegmentStyle.Dim),
                new(now, SegmentStyle.Good),
                new(meaning.Length > 0 ? $"  {meaning}" : "", SegmentStyle.Normal),
            ]);

        if (before.OriginatorCode != after.OriginatorCode) Change("ORG", before.OriginatorCode, after.OriginatorCode, after.Originator);
        if (before.EventCode != after.EventCode) Change("EVENT", before.EventCode, after.EventCode, after.Event.Name);

        var was = before.Locations.Select(l => l.Code).ToList();
        var now = after.Locations.Select(l => l.Code).ToList();
        if (!was.SequenceEqual(now))
        {
            if (was.Count == now.Count)
            {
                for (int i = 0; i < now.Count; i++)
                    if (was[i] != now[i]) Change("AREA", was[i], now[i], after.Locations[i].Description);
            }
            else
            {
                foreach (var gone in was.Except(now)) Change("AREA", gone, "(removed)");
                foreach (var loc in after.Locations.Where(l => !was.Contains(l.Code))) Change("AREA", "(missing)", loc.Code, loc.Description);
            }
        }
        if (before.ValidFor != after.ValidFor)
            Change("PURGE", $"+{before.ValidFor.Hours:00}{before.ValidFor.Minutes:00}", $"+{after.ValidFor.Hours:00}{after.ValidFor.Minutes:00}", $"valid {after.ValidFor.TotalMinutes:0} min");
        if (before.IssuedUtc != after.IssuedUtc)
            Change("ISSUED", $"{before.IssuedUtc.DayOfYear:000}{before.IssuedUtc:HHmm}", $"{after.IssuedUtc.DayOfYear:000}{after.IssuedUtc:HHmm}", $"{after.IssuedUtc:HH:mm} UTC");
        if (before.StationId != after.StationId) Change("STATION", before.StationId, after.StationId);
        return lines;
    }

    public static List<IReadOnlyList<LogSegment>> HeaderLines(SameHeader h)
    {
        var lines = new List<IReadOnlyList<LogSegment>>();
        void Field(string name, string code, string meaning) =>
            lines.Add([new(name.PadRight(9), SegmentStyle.Dim), new(code.PadRight(10), SegmentStyle.Code), new(meaning, SegmentStyle.Normal)]);

        Field("ORG", h.OriginatorCode, h.Originator);
        Field("EVENT", h.EventCode, $"{h.Event.Name} ({h.Event.Severity.ToString().ToLowerInvariant()})");
        foreach (var loc in h.Locations) Field("AREA", loc.Code, loc.Description);
        Field("PURGE", $"+{h.ValidFor.Hours:00}{h.ValidFor.Minutes:00}", $"valid {h.ValidFor.TotalMinutes:0} min · expires {h.ExpiresUtc.ToLocalTime():h:mm tt}");
        Field("ISSUED", $"{h.IssuedUtc.DayOfYear:000}{h.IssuedUtc:HHmm}",
            $"day {h.IssuedUtc.DayOfYear}, {h.IssuedUtc:HH:mm} UTC ({h.IssuedUtc.ToLocalTime().ToString("ddd MMM d, h:mm tt", CultureInfo.CurrentCulture)} local)");
        Field("STATION", h.StationId, "sender ID");
        return lines;
    }
}
