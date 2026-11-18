namespace SameEasDecoder.Core.Same;

public enum SameSeverity { Emergency, Warning, Watch, Statement, Test, Info }

public sealed record SameEventInfo(string Code, string Name, SameSeverity Severity);

public static class SameCodes
{
    private static readonly Dictionary<string, string> Originators = new()
    {
        ["EAS"] = "EAS Participant (broadcast / cable)",
        ["CIV"] = "Civil authorities",
        ["WXR"] = "National Weather Service",
        ["PEP"] = "Primary Entry Point System (FEMA)",
        ["EAN"] = "Emergency Action Notification Network",
    };

    private static readonly Dictionary<string, SameEventInfo> Events = Build(
        ("EAN", "Emergency Action Notification", SameSeverity.Emergency),
        ("EAT", "Emergency Action Termination", SameSeverity.Emergency),
        ("NIC", "National Information Center", SameSeverity.Statement),
        ("NPT", "National Periodic Test", SameSeverity.Test),
        ("NAT", "National Audible Test", SameSeverity.Test),
        ("NST", "National Silent Test", SameSeverity.Test),
        ("RMT", "Required Monthly Test", SameSeverity.Test),
        ("RWT", "Required Weekly Test", SameSeverity.Test),
        ("DMO", "Practice / Demo Warning", SameSeverity.Test),
        ("ADR", "Administrative Message", SameSeverity.Statement),
        ("AVA", "Avalanche Watch", SameSeverity.Watch),
        ("AVW", "Avalanche Warning", SameSeverity.Warning),
        ("BHW", "Biological Hazard Warning", SameSeverity.Warning),
        ("BLU", "Blue Alert", SameSeverity.Warning),
        ("BWW", "Boil Water Warning", SameSeverity.Warning),
        ("BZW", "Blizzard Warning", SameSeverity.Warning),
        ("CAE", "Child Abduction Emergency", SameSeverity.Warning),
        ("CDW", "Civil Danger Warning", SameSeverity.Warning),
        ("CEM", "Civil Emergency Message", SameSeverity.Warning),
        ("CFA", "Coastal Flood Watch", SameSeverity.Watch),
        ("CFW", "Coastal Flood Warning", SameSeverity.Warning),
        ("CHW", "Chemical Hazard Warning", SameSeverity.Warning),
        ("CWW", "Contaminated Water Warning", SameSeverity.Warning),
        ("DBA", "Dam Watch", SameSeverity.Watch),
        ("DBW", "Dam Break Warning", SameSeverity.Warning),
        ("DEW", "Contagious Disease Warning", SameSeverity.Warning),
        ("DSW", "Dust Storm Warning", SameSeverity.Warning),
        ("EQW", "Earthquake Warning", SameSeverity.Warning),
        ("EVA", "Evacuation Watch", SameSeverity.Watch),
        ("EVI", "Evacuation Immediate", SameSeverity.Warning),
        ("EWW", "Extreme Wind Warning", SameSeverity.Warning),
        ("FCW", "Food Contamination Warning", SameSeverity.Warning),
        ("FFA", "Flash Flood Watch", SameSeverity.Watch),
        ("FFS", "Flash Flood Statement", SameSeverity.Statement),
        ("FFW", "Flash Flood Warning", SameSeverity.Warning),
        ("FLA", "Flood Watch", SameSeverity.Watch),
        ("FLS", "Flood Statement", SameSeverity.Statement),
        ("FLW", "Flood Warning", SameSeverity.Warning),
        ("FRW", "Fire Warning", SameSeverity.Warning),
        ("FSW", "Flash Freeze Warning", SameSeverity.Warning),
        ("FZW", "Freeze Warning", SameSeverity.Warning),
        ("HLS", "Hurricane Local Statement", SameSeverity.Statement),
        ("HMW", "Hazardous Materials Warning", SameSeverity.Warning),
        ("HUA", "Hurricane Watch", SameSeverity.Watch),
        ("HUW", "Hurricane Warning", SameSeverity.Warning),
        ("HWA", "High Wind Watch", SameSeverity.Watch),
        ("HWW", "High Wind Warning", SameSeverity.Warning),
        ("IBW", "Iceberg Warning", SameSeverity.Warning),
        ("IFW", "Industrial Fire Warning", SameSeverity.Warning),
        ("LAE", "Local Area Emergency", SameSeverity.Warning),
        ("LEW", "Law Enforcement Warning", SameSeverity.Warning),
        ("LSW", "Land Slide Warning", SameSeverity.Warning),
        ("MEP", "Missing and Endangered Persons", SameSeverity.Warning),
        ("NMN", "Network Message Notification", SameSeverity.Statement),
        ("NUW", "Nuclear Power Plant Warning", SameSeverity.Warning),
        ("POS", "Power Outage Advisory", SameSeverity.Statement),
        ("RHW", "Radiological Hazard Warning", SameSeverity.Warning),
        ("SMW", "Special Marine Warning", SameSeverity.Warning),
        ("SPS", "Special Weather Statement", SameSeverity.Statement),
        ("SPW", "Shelter in Place Warning", SameSeverity.Warning),
        ("SQW", "Snow Squall Warning", SameSeverity.Warning),
        ("SSA", "Storm Surge Watch", SameSeverity.Watch),
        ("SSW", "Storm Surge Warning", SameSeverity.Warning),
        ("SVA", "Severe Thunderstorm Watch", SameSeverity.Watch),
        ("SVR", "Severe Thunderstorm Warning", SameSeverity.Warning),
        ("SVS", "Severe Weather Statement", SameSeverity.Statement),
        ("TOA", "Tornado Watch", SameSeverity.Watch),
        ("TOE", "911 Telephone Outage Emergency", SameSeverity.Warning),
        ("TOR", "Tornado Warning", SameSeverity.Warning),
        ("TRA", "Tropical Storm Watch", SameSeverity.Watch),
        ("TRW", "Tropical Storm Warning", SameSeverity.Warning),
        ("TSA", "Tsunami Watch", SameSeverity.Watch),
        ("TSW", "Tsunami Warning", SameSeverity.Warning),
        ("TXB", "Transmitter Backup On", SameSeverity.Info),
        ("TXF", "Transmitter Carrier Off", SameSeverity.Info),
        ("TXO", "Transmitter Carrier On", SameSeverity.Info),
        ("TXP", "Transmitter Primary On", SameSeverity.Info),
        ("VOW", "Volcano Warning", SameSeverity.Warning),
        ("WFA", "Wild Fire Watch", SameSeverity.Watch),
        ("WFW", "Wild Fire Warning", SameSeverity.Warning),
        ("WSA", "Winter Storm Watch", SameSeverity.Watch),
        ("WSW", "Winter Storm Warning", SameSeverity.Warning));

    private static readonly Dictionary<int, (string Abbr, string Name)> States = new()
    {
        [1] = ("AL", "Alabama"), [2] = ("AK", "Alaska"), [4] = ("AZ", "Arizona"), [5] = ("AR", "Arkansas"),
        [6] = ("CA", "California"), [8] = ("CO", "Colorado"), [9] = ("CT", "Connecticut"), [10] = ("DE", "Delaware"),
        [11] = ("DC", "District of Columbia"), [12] = ("FL", "Florida"), [13] = ("GA", "Georgia"), [15] = ("HI", "Hawaii"),
        [16] = ("ID", "Idaho"), [17] = ("IL", "Illinois"), [18] = ("IN", "Indiana"), [19] = ("IA", "Iowa"),
        [20] = ("KS", "Kansas"), [21] = ("KY", "Kentucky"), [22] = ("LA", "Louisiana"), [23] = ("ME", "Maine"),
        [24] = ("MD", "Maryland"), [25] = ("MA", "Massachusetts"), [26] = ("MI", "Michigan"), [27] = ("MN", "Minnesota"),
        [28] = ("MS", "Mississippi"), [29] = ("MO", "Missouri"), [30] = ("MT", "Montana"), [31] = ("NE", "Nebraska"),
        [32] = ("NV", "Nevada"), [33] = ("NH", "New Hampshire"), [34] = ("NJ", "New Jersey"), [35] = ("NM", "New Mexico"),
        [36] = ("NY", "New York"), [37] = ("NC", "North Carolina"), [38] = ("ND", "North Dakota"), [39] = ("OH", "Ohio"),
        [40] = ("OK", "Oklahoma"), [41] = ("OR", "Oregon"), [42] = ("PA", "Pennsylvania"), [44] = ("RI", "Rhode Island"),
        [45] = ("SC", "South Carolina"), [46] = ("SD", "South Dakota"), [47] = ("TN", "Tennessee"), [48] = ("TX", "Texas"),
        [49] = ("UT", "Utah"), [50] = ("VT", "Vermont"), [51] = ("VA", "Virginia"), [53] = ("WA", "Washington"),
        [54] = ("WV", "West Virginia"), [55] = ("WI", "Wisconsin"), [56] = ("WY", "Wyoming"),
        [60] = ("AS", "American Samoa"), [66] = ("GU", "Guam"), [69] = ("MP", "Northern Mariana Islands"),
        [72] = ("PR", "Puerto Rico"), [78] = ("VI", "U.S. Virgin Islands"),
    };

    private static readonly Dictionary<int, string> MarineAreas = new()
    {
        [57] = "Eastern North Pacific / U.S. West Coast",
        [58] = "North Pacific / Alaska waters",
        [59] = "Central Pacific / Hawaiian waters",
        [61] = "South Central Pacific / American Samoa",
        [65] = "Western Pacific / Mariana Islands",
        [73] = "Western North Atlantic (north of Currituck Beach Light)",
        [75] = "Western North Atlantic (south of Currituck Beach Light)",
        [77] = "Gulf of Mexico / Gulf of America",
        [91] = "Lake Superior",
        [92] = "Lake Michigan",
        [93] = "Lake Huron",
        [94] = "Lake St. Clair",
        [96] = "Lake Erie",
        [97] = "Lake Ontario",
        [98] = "St. Lawrence River",
    };

    private static readonly string[] Subdivisions =
        ["", "Northwest", "North", "Northeast", "West", "Central", "East", "Southwest", "South", "Southeast"];

    private static Dictionary<string, SameEventInfo> Build(params (string Code, string Name, SameSeverity Sev)[] items) =>
        items.ToDictionary(i => i.Code, i => new SameEventInfo(i.Code, i.Name, i.Sev));

    public static string DescribeOriginator(string code) =>
        Originators.TryGetValue(code, out var name) ? name : $"Unknown originator ({code})";

    public static SameEventInfo GetEvent(string code)
    {
        if (Events.TryGetValue(code, out var info)) return info;
        var severity = code.Length == 3 ? code[2] switch
        {
            'W' or 'E' => SameSeverity.Warning,
            'A' => SameSeverity.Watch,
            'S' => SameSeverity.Statement,
            'T' => SameSeverity.Test,
            _ => SameSeverity.Info,
        } : SameSeverity.Info;
        return new SameEventInfo(code, $"Unrecognized event ({code})", severity);
    }

    public static bool IsKnownEvent(string code) => Events.ContainsKey(code);

    public static string? StateAbbreviation(int fips) => States.TryGetValue(fips, out var s) ? s.Abbr : null;

    public static string? StateName(int fips) => States.TryGetValue(fips, out var s) ? s.Name : null;

    public static string? MarineAreaName(int code) => MarineAreas.TryGetValue(code, out var s) ? s : null;

    public static string SubdivisionName(int p) => p >= 0 && p < Subdivisions.Length ? Subdivisions[p] : "";

    public static string? CountyName(int stateFips, int countyFips)
    {
        string? name = Geo.UsAtlas.Instance?.County(stateFips, countyFips)?.Name;
        if (string.IsNullOrEmpty(name)) return null;

        if (stateFips == 22) return name + " Parish";
        if (stateFips is 2 or 11 or 60 or 66 or 69 or 72 or 78) return name;
        if (name.EndsWith(" City", StringComparison.Ordinal)) return name;
        if (countyFips >= 510 && stateFips is 24 or 29 or 51) return name + " City";
        return name + " County";
    }
}
