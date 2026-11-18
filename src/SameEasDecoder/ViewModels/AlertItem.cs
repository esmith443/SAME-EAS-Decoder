using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SameEasDecoder.Core.Same;
using SameEasDecoder.Services;

namespace SameEasDecoder.ViewModels;

public sealed class AlertItem : ObservableObject
{
    public static Action<AlertItem>? PlaybackHandler { get; set; }

    private string? _toneText;
    private bool _eomReceived;
    private string? _recordingPath;
    private bool _isPlaying;
    private ObservableCollection<LogItem> _activityLog = [];

    public AlertItem(SameAlert alert, string origin, bool receiving = false)
    {
        Origin = origin;
        Received = alert.ReceivedUtc.ToLocalTime();
        Apply(alert, receiving);

        CopyCommand = new RelayCommand(Copy);
        PlayCommand = new RelayCommand(() => PlaybackHandler?.Invoke(this), () => HasRecording);
        ShowFileCommand = new RelayCommand(() => { if (_recordingPath is not null) AppPaths.ShowInExplorer(_recordingPath); }, () => HasRecording);
        ShowMapCommand = new RelayCommand(ShowMap);
    }

    public void Apply(SameAlert alert, bool receiving)
    {
        Source = alert;
        IsReceiving = receiving;
        var h = alert.Header;
        var ev = h.Event;

        EventName = ev.Name;
        EventCode = h.EventCode;
        Severity = ev.Severity;
        Originator = h.Originator;
        StationId = h.StationId;
        if (Areas is null || !Areas.Select(l => l.Code).SequenceEqual(h.Locations.Select(l => l.Code)))
        {
            Areas = h.Locations;
            AreaNames = h.Locations.Select(l => l.Description).ToList();
        }
        RawHeader = h.Raw;

        var issuedLocal = h.IssuedUtc.ToLocalTime();
        var expiresLocal = h.ExpiresUtc.ToLocalTime();
        IssuedText = $"{issuedLocal:ddd MMM d, h:mm tt}";
        IssuedUtcText = $"{h.IssuedUtc:HH:mm} UTC · day {h.IssuedUtc.DayOfYear:000}";
        ExpiresText = expiresLocal.Date == issuedLocal.Date ? $"{expiresLocal:h:mm tt}" : $"{expiresLocal:ddd MMM d, h:mm tt}";
        ValidForText = FormatSpan(h.ValidFor);

        VoteOk = !receiving && alert.Verified;
        VoteText = receiving
            ? alert.Vote switch
            {
                { BurstCount: 1 } => "Receiving · burst 1 of 3",
                { ExactMatches: >= 2 } => "Receiving · bursts 1 and 2 agree",
                _ => "Receiving · bursts 1 and 2 differ",
            }
            : alert.Vote switch
            {
                { BurstCount: 1 } => "1 of 3 bursts received · unverified",
                { ExactMatches: >= 2 } v => $"{v.ExactMatches} of {v.BurstCount} bursts match",
                { SoftCombined: true } v => $"Rebuilt bit by bit from {v.BurstCount} damaged bursts",
                { CharacterVote: true } v => $"Rebuilt by character vote from {v.BurstCount} bursts",
                var v => $"{v.BurstCount} bursts · no exact match",
            };
        if (!receiving && alert.Vote.RepairedCharacters > 0)
            VoteText += $" · {alert.Vote.RepairedCharacters} char repaired";

        Raise(string.Empty);
    }

    public SameAlert Source { get; private set; } = null!;

    public bool IsReceiving { get; private set; }

    public string Origin { get; }
    public string EventName { get; private set; } = "";
    public string EventCode { get; private set; } = "";
    public SameSeverity Severity { get; private set; }
    public string Originator { get; private set; } = "";
    public string StationId { get; private set; } = "";
    public IReadOnlyList<SameLocation> Areas { get; private set; } = null!;
    public IReadOnlyList<string> AreaNames { get; private set; } = [];
    public string RawHeader { get; private set; } = "";
    public DateTime Received { get; }
    public string ReceivedText => Received.Date == DateTime.Today ? $"{Received:h:mm:ss tt}" : $"{Received:MMM d, h:mm tt}";
    public string ReceivedLong => Received.ToString("ddd MMM d yyyy, h:mm:ss tt", CultureInfo.CurrentCulture);
    public string IssuedText { get; private set; } = "";
    public string IssuedUtcText { get; private set; } = "";
    public string ExpiresText { get; private set; } = "";
    public string ValidForText { get; private set; } = "";
    public bool VoteOk { get; private set; }
    public string VoteText { get; private set; } = "";
    public string OriginLine => StationId.Length > 0 ? $"{Originator} · {StationId}" : Originator;

    public string AreaSummary
    {
        get
        {
            if (AreaNames.Count == 0) return "";
            string first = AreaNames[0];
            return AreaNames.Count == 1 ? first : $"{first} +{AreaNames.Count - 1} more";
        }
    }

    public string SeverityLabel => Severity switch
    {
        SameSeverity.Emergency => "EMERGENCY",
        SameSeverity.Warning => "WARNING",
        SameSeverity.Watch => "WATCH",
        SameSeverity.Statement => "ADVISORY",
        SameSeverity.Test => "TEST",
        _ => "INFO",
    };

    private string SeverityKey => Severity switch
    {
        SameSeverity.Emergency => "Rose",
        SameSeverity.Warning => "Red",
        SameSeverity.Watch => "Amber",
        SameSeverity.Statement => "Teal",
        _ => "Slate",
    };

    public Brush SeverityBrush => (Brush)Application.Current.FindResource(SeverityKey);
    public Color SeverityColor => ((SolidColorBrush)SeverityBrush).Color;

    public string ExpiryState
    {
        get
        {
            var remaining = Source.Header.ExpiresUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return "Expired";
            return remaining.TotalMinutes < 1 ? "Expires in < 1 min" : $"{FormatSpan(remaining)} left";
        }
    }

    public bool IsExpired => Source.Header.ExpiresUtc <= DateTime.UtcNow;

    public string? ToneText { get => _toneText; set { if (Set(ref _toneText, value)) Raise(nameof(HasTone)); } }
    public bool HasTone => _toneText is not null;
    public bool EomReceived { get => _eomReceived; set => Set(ref _eomReceived, value); }

    public string? RecordingPath
    {
        get => _recordingPath;
        set
        {
            if (!Set(ref _recordingPath, value)) return;
            Raise(nameof(HasRecording));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasRecording => _recordingPath is not null && File.Exists(_recordingPath);

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (Set(ref _isPlaying, value)) { Raise(nameof(PlayGlyph)); Raise(nameof(PlayText)); } }
    }

    public string PlayGlyph => _isPlaying ? "" : "";
    public string PlayText => _isPlaying ? "Stop" : "Play audio";

    public ICommand CopyCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand ShowFileCommand { get; }
    public ICommand ShowMapCommand { get; }

    public ObservableCollection<LogItem> ActivityLog => _activityLog;
    public bool HasActivityLog => _activityLog.Count > 0;

    public void AttachActivityLog(ObservableCollection<LogItem> log)
    {
        _activityLog.CollectionChanged -= OnActivityChanged;
        _activityLog = log;
        log.CollectionChanged += OnActivityChanged;
        Raise(nameof(ActivityLog));
        Raise(nameof(HasActivityLog));
    }

    private void OnActivityChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Raise(nameof(ActivityLog));
        Raise(nameof(HasActivityLog));
    }

    public void RefreshTime()
    {
        Raise(nameof(ExpiryState));
        Raise(nameof(IsExpired));
        Raise(nameof(ReceivedText));
    }

    public string ToReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{EventName} ({EventCode}) — {SeverityLabel}");
        sb.AppendLine($"From: {OriginLine}");
        sb.AppendLine($"Areas: {string.Join("; ", AreaNames)}");
        sb.AppendLine($"Issued: {IssuedText} ({IssuedUtcText})");
        sb.AppendLine($"Expires: {ExpiresText} (valid {ValidForText})");
        sb.AppendLine($"Received: {Received.ToString("G", CultureInfo.CurrentCulture)} via {Origin} · {VoteText}");
        sb.Append($"Header: {RawHeader}");
        return sb.ToString();
    }

    public HistoryEntry ToHistory() => new()
    {
        ReceivedUtc = Source.ReceivedUtc,
        Header = RawHeader,
        Bursts = Source.Vote.BurstCount,
        ExactMatches = Source.Vote.ExactMatches,
        CharacterVote = Source.Vote.CharacterVote,
        SoftCombined = Source.Vote.SoftCombined,
        RepairedCharacters = Source.Vote.RepairedCharacters,
        Origin = Origin,
        Tone = ToneText,
        Eom = EomReceived,
        Recording = RecordingPath,
        Log = ActivityLog.ToList(),
    };

    public static AlertItem? FromHistory(HistoryEntry e)
    {
        var received = DateTime.SpecifyKind(e.ReceivedUtc, DateTimeKind.Utc);
        if (!SameHeader.TryParse(e.Header, received, out var header)) return null;
        var vote = new SameVoteResult(e.Header, e.Bursts, e.ExactMatches, e.CharacterVote) { SoftCombined = e.SoftCombined, RepairedCharacters = e.RepairedCharacters };
        var item = new AlertItem(new SameAlert(header!, vote, [], received), e.Origin ?? "")
        {
            _toneText = e.Tone,
            _eomReceived = e.Eom,
            _recordingPath = e.Recording,
        };
        if (e.Log is { Count: > 0 }) item.AttachActivityLog(new ObservableCollection<LogItem>(e.Log));
        return item;
    }

    private void Copy()
    {
        try { Clipboard.SetText(ToReport()); } catch (Exception) { }
    }

    private void ShowMap()
    {
        var window = new MapWindow(this) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    private static string FormatSpan(TimeSpan t)
    {
        if (t.TotalMinutes < 60) return $"{(int)Math.Ceiling(t.TotalMinutes)} min";
        if (t.TotalHours >= 48) return $"{(int)Math.Round(t.TotalDays)} days";
        int h = (int)t.TotalHours, m = (int)Math.Round(t.TotalMinutes - h * 60);
        if (m == 60) { h++; m = 0; }
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }
}
