using System.Text;

namespace SameEasDecoder.Core.Same;

public sealed record SameVoteResult(string Text, int BurstCount, int ExactMatches, bool CharacterVote)
{
    public bool SoftCombined { get; init; }

    public int RepairedCharacters { get; init; }
}

public sealed record SameAlert(SameHeader Header, SameVoteResult Vote, IReadOnlyList<string> RawBursts, DateTime ReceivedUtc)
{
    public bool Verified => Vote.ExactMatches >= 2;
}

public sealed record SameEndOfMessage(int BurstCount, DateTime ReceivedUtc);

public static class SameVoting
{
    public static SameVoteResult Vote(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return new SameVoteResult("", 0, 0, false);

        var best = texts.GroupBy(t => t, StringComparer.Ordinal).OrderByDescending(g => g.Count()).First();
        if (best.Count() >= 2 || texts.Count == 1)
            return new SameVoteResult(best.Key, texts.Count, best.Count(), false);

        int length = texts.GroupBy(t => t.Length)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key)
            .First().Key;

        var sb = new StringBuilder(length);
        var counts = new Dictionary<char, int>();
        for (int i = 0; i < length; i++)
        {
            counts.Clear();
            char first = '?';
            foreach (string t in texts)
            {
                if (i >= t.Length || t[i] == '?') continue;
                if (first == '?') first = t[i];
                counts[t[i]] = counts.GetValueOrDefault(t[i]) + 1;
            }
            if (counts.Count == 0) { sb.Append('?'); continue; }
            int max = counts.Values.Max();
            sb.Append(counts[first] == max ? first : counts.First(kv => kv.Value == max).Key);
        }

        string result = sb.ToString();
        return new SameVoteResult(result, texts.Count, texts.Count(t => t == result), true);
    }

    public static SameVoteResult Vote(IReadOnlyList<SameBurst> bursts)
    {
        var texts = bursts.Select(b => b.Text).ToList();
        var hard = Vote(texts);
        if (bursts.Count < 2 || hard.ExactMatches >= 2) return hard;

        int length = bursts.GroupBy(b => b.RawBytes.Length).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).First().Key;
        var usable = bursts.Where(b => b.RawBytes.Length == length && b.SoftBits.Length == length * 8).ToList();
        if (usable.Count < 2) return hard;

        var bytes = new byte[length];
        for (int i = 0; i < length * 8; i++)
        {
            double sum = 0;
            foreach (var b in usable) sum += Math.Clamp(b.SoftBits[i] / 0.4, -1, 1);
            if (sum >= 0) bytes[i / 8] |= (byte)(1 << (i % 8));
        }
        var sb = new StringBuilder(length);
        foreach (byte v in bytes) sb.Append(SameBurst.IsValidByte(v) ? (char)v : '?');
        string text = sb.ToString();
        if (SameBurst.Classify(text) == SameBurstKind.Header && !text.StartsWith("ZCZC", StringComparison.Ordinal)) text = "ZCZC" + text[4..];
        return new SameVoteResult(text, bursts.Count, texts.Count(t => t == text), true) { SoftCombined = true };
    }
}

public sealed class SameMessageAssembler
{
    private readonly List<SameBurst> _pending = [];
    private readonly long _timeoutSamples;
    private SameBurstKind _pendingKind;
    private long _lastBurstEnd;

    public SameMessageAssembler(int sampleRate, double groupTimeoutSeconds = 2.5)
    {
        _timeoutSamples = (long)(sampleRate * groupTimeoutSeconds);
    }

    public Func<DateTime> UtcClock { get; set; } = () => DateTime.UtcNow;

    public event Action<SameAlert>? AlertDecoded;

    public event Action<SameAlert>? AlertProgress;

    public event Action<SameEndOfMessage>? EndOfMessage;
    public event Action<string, IReadOnlyList<string>>? HeaderRejected;

    public void AddBurst(SameBurst burst)
    {
        if (burst.Kind == SameBurstKind.Unknown) return;

        if (_pending.Count > 0 && (burst.Kind != _pendingKind || burst.StartSample - _lastBurstEnd > _timeoutSamples))
            Complete();

        _pending.Add(burst);
        _pendingKind = burst.Kind;
        _lastBurstEnd = burst.EndSample;
        if (_pending.Count >= 3)
        {
            Complete();
        }
        else if (burst.Kind == SameBurstKind.Header && TryBuild(_pending.ToList(), UtcClock()) is { } provisional)
        {
            AlertProgress?.Invoke(provisional);
        }
    }

    public void Advance(long samplePosition)
    {
        if (_pending.Count > 0 && samplePosition - _lastBurstEnd > _timeoutSamples) Complete();
    }

    public void Flush()
    {
        if (_pending.Count > 0) Complete();
    }

    public void Reset() => _pending.Clear();

    private void Complete()
    {
        var bursts = _pending.ToList();
        var kind = _pendingKind;
        _pending.Clear();
        var now = UtcClock();

        if (kind == SameBurstKind.EndOfMessage)
        {
            EndOfMessage?.Invoke(new SameEndOfMessage(bursts.Count, now));
            return;
        }

        if (TryBuild(bursts, now) is { } alert)
        {
            AlertDecoded?.Invoke(alert);
            return;
        }
        HeaderRejected?.Invoke(SameVoting.Vote(bursts).Text, bursts.Select(b => b.Text).ToList());
    }

    private static SameAlert? TryBuild(List<SameBurst> bursts, DateTime now)
    {
        var texts = bursts.Select(b => b.Text).ToList();
        var vote = SameVoting.Vote(bursts);
        if (SameHeader.TryParse(vote.Text, now, out var header)) return new SameAlert(header!, vote, texts, now);

        var hard = SameVoting.Vote(texts);
        if (SameHeader.TryParse(hard.Text, now, out header)) return new SameAlert(header!, hard, texts, now);

        foreach (var group in texts.GroupBy(b => b).OrderByDescending(g => g.Count()))
        {
            if (SameHeader.TryParse(group.Key, now, out header))
                return new SameAlert(header!, new SameVoteResult(group.Key, texts.Count, group.Count(), false), texts, now);
        }

        foreach (var candidate in new[] { vote, hard }.Concat(texts.Select(t => new SameVoteResult(t, texts.Count, 1, false))))
        {
            if (SameHeaderRepair.TryRepair(candidate.Text, out var fixedText, out int changes) && SameHeader.TryParse(fixedText, now, out header))
                return new SameAlert(header!, candidate with { Text = fixedText, RepairedCharacters = changes }, texts, now);
        }
        return null;
    }
}
