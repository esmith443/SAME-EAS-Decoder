using System.Globalization;
using System.Text.RegularExpressions;

namespace SameEasDecoder.Core.Same;

public sealed record SameLocation(string Code, int Subdivision, int StateFips, int CountyFips)
{
    public bool IsMarine => SameCodes.StateName(StateFips) is null && SameCodes.MarineAreaName(StateFips) is not null;

    public string Description
    {
        get
        {
            string part = SameCodes.SubdivisionName(Subdivision);
            string prefix = part.Length > 0 ? $"{part} " : "";

            if (StateFips == 0) return "Entire United States";

            if (SameCodes.MarineAreaName(StateFips) is { } marine && SameCodes.StateName(StateFips) is null)
                return $"{prefix}Marine zone {CountyFips:000} – {marine}";

            string? abbr = SameCodes.StateAbbreviation(StateFips);
            if (abbr is null) return $"{prefix}Area {Code}";
            if (CountyFips == 0) return $"{prefix}All of {SameCodes.StateName(StateFips)}".Trim();

            string? county = SameCodes.CountyName(StateFips, CountyFips);
            string where = county is null ? $"County {StateFips:00}{CountyFips:000}, {abbr}" : $"{county}, {abbr}";
            return part.Length > 0 ? $"{part} {where}" : where;
        }
    }
}

public sealed partial class SameHeader
{
    [GeneratedRegex(@"^ZCZC-(?<org>[A-Z]{3})-(?<evt>[A-Z0-9]{3})-(?<locs>\d{6}(?:-\d{6}){0,30})\+(?<purge>\d{4})-(?<day>\d{3})(?<hh>\d{2})(?<mm>\d{2})-(?<id>[^-]{1,8})-?")]
    private static partial Regex HeaderRegex();

    private SameHeader() { }

    public required string Raw { get; init; }
    public required string OriginatorCode { get; init; }
    public required string EventCode { get; init; }
    public required IReadOnlyList<SameLocation> Locations { get; init; }
    public required TimeSpan ValidFor { get; init; }
    public required DateTime IssuedUtc { get; init; }
    public required string StationId { get; init; }

    public DateTime ExpiresUtc => IssuedUtc + ValidFor;
    public string Originator => SameCodes.DescribeOriginator(OriginatorCode);
    public SameEventInfo Event => SameCodes.GetEvent(EventCode);

    public static bool TryParse(string text, DateTime referenceUtc, out SameHeader? header)
    {
        header = null;
        var m = HeaderRegex().Match(text.Trim());
        if (!m.Success) return false;

        int day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
        int hh = int.Parse(m.Groups["hh"].Value, CultureInfo.InvariantCulture);
        int mm = int.Parse(m.Groups["mm"].Value, CultureInfo.InvariantCulture);
        int ph = int.Parse(m.Groups["purge"].Value.AsSpan(0, 2), CultureInfo.InvariantCulture);
        int pm = int.Parse(m.Groups["purge"].Value.AsSpan(2, 2), CultureInfo.InvariantCulture);
        if (day < 1 || day > 366 || hh > 23 || mm > 59 || pm > 59) return false;

        var locations = new List<SameLocation>();
        foreach (string code in m.Groups["locs"].Value.Split('-'))
        {
            locations.Add(new SameLocation(code,
                code[0] - '0',
                int.Parse(code.AsSpan(1, 2), CultureInfo.InvariantCulture),
                int.Parse(code.AsSpan(3, 3), CultureInfo.InvariantCulture)));
        }

        header = new SameHeader
        {
            Raw = m.Value.EndsWith('-') ? m.Value : m.Value + "-",
            OriginatorCode = m.Groups["org"].Value,
            EventCode = m.Groups["evt"].Value,
            Locations = locations,
            ValidFor = new TimeSpan(ph, pm, 0),
            IssuedUtc = ResolveIssueTime(day, hh, mm, referenceUtc),
            StationId = m.Groups["id"].Value.Trim(),
        };
        return true;
    }

    private static DateTime ResolveIssueTime(int day, int hh, int mm, DateTime referenceUtc)
    {
        int year = referenceUtc.Year;
        DateTime Build(int y) => new DateTime(y, 1, 1, hh, mm, 0, DateTimeKind.Utc).AddDays(Math.Min(day, DateTime.IsLeapYear(y) ? 366 : 365) - 1);
        var t = Build(year);
        if (t > referenceUtc.AddDays(2)) t = Build(year - 1);
        return t;
    }
}
