using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace SameEasDecoder.ViewModels;

public enum LogKind { Info, Data, Tone, Alert, Success, Error, Warning }

public enum SegmentStyle { Normal, Dim, Good, Mismatch, Error, Code }

public sealed record LogSegment(string Text, SegmentStyle Style = SegmentStyle.Normal);

public sealed record LogItem(DateTime Time, LogKind Kind, string Message, IReadOnlyList<IReadOnlyList<LogSegment>>? Lines = null)
{
    [JsonIgnore]
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    [JsonIgnore]
    public bool HasLines => Lines is { Count: > 0 };

    [JsonIgnore]
    public Brush DotBrush => (Brush)Application.Current.FindResource(Kind switch
    {
        LogKind.Data => "Accent",
        LogKind.Tone => "Amber",
        LogKind.Warning => "Amber",
        LogKind.Alert => "Red",
        LogKind.Success => "Green",
        LogKind.Error => "Rose",
        _ => "Text3",
    });

    public override string ToString()
    {
        var sb = new StringBuilder($"{Time:yyyy-MM-dd HH:mm:ss}  {Message}");
        if (Lines is not null)
            foreach (var line in Lines) sb.Append(Environment.NewLine).Append("                     ").Append(string.Concat(line.Select(s => s.Text)));
        return sb.ToString();
    }
}
