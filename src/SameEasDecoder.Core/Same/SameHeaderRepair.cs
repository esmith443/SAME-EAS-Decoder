using System.Numerics;
using System.Text;

namespace SameEasDecoder.Core.Same;

public static class SameHeaderRepair
{
    public const int MaxBitDistance = 2;

    private enum Slot { Dash, Plus, Digit, Upper, Any }

    public static bool TryRepair(string text, out string repaired, out int changes)
    {
        repaired = text;
        changes = 0;
        var layout = Layout(text.Length);
        if (layout is null) return false;

        var sb = new StringBuilder(text);
        for (int i = 0; i < layout.Length; i++)
        {
            char c = sb[i];
            if (Fits(c, layout[i], i)) continue;
            char? fix = Nearest(c, layout[i], i);
            if (fix is null) return false;
            sb[i] = fix.Value;
            changes++;
        }
        repaired = sb.ToString();
        return changes > 0;
    }

    private static Slot[]? Layout(int length)
    {
        int areas = (length - 35) / 7;
        if (length < 42 || (length - 35) % 7 != 0 || areas < 1 || areas > 31) return null;
        var s = new List<Slot>();
        s.AddRange([Slot.Upper, Slot.Upper, Slot.Upper, Slot.Upper, Slot.Dash]);
        s.AddRange([Slot.Upper, Slot.Upper, Slot.Upper, Slot.Dash]);
        s.AddRange([Slot.Upper, Slot.Upper, Slot.Upper, Slot.Dash]);
        for (int a = 0; a < areas; a++)
        {
            for (int k = 0; k < 6; k++) s.Add(Slot.Digit);
            s.Add(a == areas - 1 ? Slot.Plus : Slot.Dash);
        }
        s.AddRange([Slot.Digit, Slot.Digit, Slot.Digit, Slot.Digit, Slot.Dash]);
        for (int k = 0; k < 7; k++) s.Add(Slot.Digit);
        s.Add(Slot.Dash);
        for (int k = 0; k < 8; k++) s.Add(Slot.Any);
        s.Add(Slot.Dash);
        return s.ToArray();
    }

    private static bool Fits(char c, Slot slot, int index) => slot switch
    {
        Slot.Dash => c == '-',
        Slot.Plus => c == '+',
        Slot.Digit => c is >= '0' and <= '9',
        Slot.Upper => index < 4 ? c == "ZCZC"[index] : c is >= 'A' and <= 'Z',
        _ => c is >= ' ' and <= '~' and not '-' and not '?',
    };

    private static char? Nearest(char c, Slot slot, int index)
    {
        if (c == '?') return slot switch { Slot.Dash => '-', Slot.Plus => '+', _ when index < 4 => "ZCZC"[index], _ => null };
        IEnumerable<char> candidates = slot switch
        {
            Slot.Dash => ['-'],
            Slot.Plus => ['+'],
            Slot.Digit => "0123456789",
            Slot.Upper => index < 4 ? [ "ZCZC"[index] ] : Enumerable.Range('A', 26).Select(x => (char)x),
            _ => [],
        };
        char? best = null;
        int bestDistance = int.MaxValue, ties = 0;
        foreach (char k in candidates)
        {
            int dist = BitOperations.PopCount((uint)(k ^ c));
            if (dist < bestDistance) { best = k; bestDistance = dist; ties = 1; }
            else if (dist == bestDistance) ties++;
        }
        return bestDistance <= MaxBitDistance && ties == 1 ? best : null;
    }
}
