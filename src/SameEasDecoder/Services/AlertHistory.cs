using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SameEasDecoder.ViewModels;

namespace SameEasDecoder.Services;

public sealed class HistoryEntry
{
    public DateTime ReceivedUtc { get; set; }
    public string Header { get; set; } = "";
    public int Bursts { get; set; }
    public int ExactMatches { get; set; }
    public bool CharacterVote { get; set; }
    public bool SoftCombined { get; set; }
    public int RepairedCharacters { get; set; }
    public string? Origin { get; set; }
    public string? Tone { get; set; }
    public bool Eom { get; set; }
    public string? Recording { get; set; }
    public List<SameEasDecoder.ViewModels.LogItem>? Log { get; set; }
}

public static class AlertHistory
{
    public const int MaxEntries = 5000;

    public static string FilePath => Path.Combine(AppPaths.DataFolder, "history.jsonl");

    public static List<HistoryEntry> Load()
    {
        var result = new List<HistoryEntry>();
        try
        {
            if (!File.Exists(FilePath)) return result;
            foreach (string line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<HistoryEntry>(line) is { Header.Length: > 0 } e) result.Add(e);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    public static void Save(IEnumerable<HistoryEntry> entriesOldestFirst)
    {
        Directory.CreateDirectory(AppPaths.DataFolder);
        string temp = FilePath + ".tmp";
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            foreach (var e in entriesOldestFirst.TakeLast(MaxEntries)) writer.WriteLine(JsonSerializer.Serialize(e));
        }
        File.Move(temp, FilePath, overwrite: true);
    }

    public static void ExportCsv(string path, IEnumerable<AlertItem> items)
    {
        static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("Received,Event code,Event,Severity,Originator,Station,Areas,Issued,Expires,Bursts,Attention tone,End of message,Source,Recording,Header");
        foreach (var a in items)
        {
            var h = a.Source.Header;
            sb.AppendLine(string.Join(',',
                Q(a.Received.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Q(a.EventCode), Q(a.EventName), Q(a.SeverityLabel), Q(a.Originator), Q(a.StationId),
                Q(string.Join("; ", a.AreaNames)),
                Q(h.IssuedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Q(h.ExpiresUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                Q(a.VoteText), Q(a.ToneText), Q(a.EomReceived ? "yes" : "no"), Q(a.Origin), Q(a.RecordingPath), Q(a.RawHeader)));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }
}
