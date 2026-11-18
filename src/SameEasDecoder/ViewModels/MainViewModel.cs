using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SameEasDecoder.Audio;
using SameEasDecoder.Core;
using SameEasDecoder.Core.Same;
using SameEasDecoder.Services;

namespace SameEasDecoder.ViewModels;

public sealed record ChannelOption(ChannelMode Mode, string Label);

public sealed record SeverityFilter(string Label, Func<AlertItem, bool> Match);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLogItems = 1000;

    private readonly DecoderPipeline _pipeline = new();
    private readonly AudioEngine _engine;
    private readonly DecoderPipeline _playbackPipeline = new();
    private readonly AudioEngine _playbackEngine;
    private readonly DispatcherTimer _historySaveTimer;
    private readonly AppSettings _settings;
    private readonly Dispatcher _ui;
    private AlertItem? _playing;
    private AlertItem? _selectedHistory;
    private SeverityFilter _historyFilter;
    private string _historySearch = "";
    private bool _isHistoryTab;
    private double _playbackProgress;
    private string _playbackText = "";
    private AudioDevice? _selectedDevice;
    private ChannelOption _selectedChannel;
    private AlertItem? _current;
    private DateTime _currentAt, _eomUntil, _lastExpiryRefresh;
    private AudioDevice? _resumeDevice;
    private AudioDevice? _pausedForPlayback;
    private bool _suppressDeviceRestart;
    private ObservableCollection<LogItem>? _activationLog;
    private AlertItem? _activationItem;
    private AlertItem? _receiving;
    private long _lastBurstEnd = -1;
    private SameBurstKind _lastBurstKind;
    private int _burstNumber;
    private bool _sameActive, _easTone, _nwrTone, _recording, _eomActive;
    private double _progress;
    private string _progressText = "";

    public MainViewModel(AppSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
        _engine = new AudioEngine(_pipeline);

        ChannelOptions =
        [
            new(ChannelMode.Mix, "Both channels"),
            new(ChannelMode.Left, "Left channel"),
            new(ChannelMode.Right, "Right channel"),
        ];
        _selectedChannel = ChannelOptions.FirstOrDefault(c => c.Mode.ToString() == settings.ChannelMode) ?? ChannelOptions[0];
        _pipeline.ChannelMode = _selectedChannel.Mode;
        _pipeline.RecordingDirectory = AppPaths.RecordingsFolder;
        _pipeline.RecordingEnabled = settings.RecordAudio;

        _pipeline.SameBurstDecoded += b => Post(() => OnBurst(b));
        _pipeline.SameAlertDecoded += a => Post(() => OnAlert(a));
        _pipeline.SameAlertProgress += a => Post(() => OnAlertProgress(a));
        _pipeline.SameEndOfMessage += e => Post(() => OnEndOfMessage(e));
        _pipeline.SameHeaderRejected += (t, bursts) => Post(() => OnHeaderRejected(t, bursts));
        _pipeline.SameSyncFailed += f => Post(() => AddLog(LogKind.Warning, $"SAME signal heard but not readable — {f.Reason}",
            VerboseFormat.SyncFailureLines(f), activation: _activationLog is not null));
        _pipeline.AttentionToneStarted += k => Post(() => AddLog(LogKind.Tone, $"{ToneName(k)} detected", activation: _activationLog is not null));
        _pipeline.AttentionToneEnded += (k, d) => Post(() => OnToneEnded(k, d));
        _pipeline.RecordingSaved += r => Post(() => OnRecordingSaved(r));
        _pipeline.Diagnostic += m => Post(() => AddLog(LogKind.Error, m));
        _engine.Failed += m => Post(() => OnSourceFailed(m));
        _engine.SourceCompleted += () => Post(OnSourceCompleted);

        _playbackEngine = new AudioEngine(_playbackPipeline);
        _playbackEngine.SourceCompleted += () => Post(StopPlayback);
        _playbackEngine.Failed += m => Post(() => { AddLog(LogKind.Error, $"Playback stopped: {m}"); StopPlayback(); });
        AlertItem.PlaybackHandler = TogglePlayback;
        StopPlaybackCommand = new RelayCommand(StopPlayback, () => _playing is not null);

        HistoryFilters =
        [
            new("All alerts", _ => true),
            new("Warnings & emergencies", a => a.Severity is SameSeverity.Warning or SameSeverity.Emergency),
            new("Watches", a => a.Severity == SameSeverity.Watch),
            new("Advisories & statements", a => a.Severity == SameSeverity.Statement),
            new("Tests", a => a.Severity is SameSeverity.Test or SameSeverity.Info),
            new("With recordings", a => a.HasRecording),
        ];
        _historyFilter = HistoryFilters[0];
        HistoryView = CollectionViewSource.GetDefaultView(History);
        HistoryView.Filter = o => o is AlertItem a && _historyFilter.Match(a) && MatchesSearch(a);
        History.CollectionChanged += (_, _) => RaiseHistoryCounts();
        _historySaveTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SaveHistoryNow(), ui) { IsEnabled = false };
        LoadHistory();

        DeleteHistoryCommand = new RelayCommand(p => DeleteHistory(p as AlertItem ?? SelectedHistory), p => (p as AlertItem ?? SelectedHistory) is not null);
        ClearHistoryCommand = new RelayCommand(ClearHistory, () => History.Count > 0);
        ExportHistoryCommand = new RelayCommand(ExportHistory, () => History.Count > 0);
        ShowLiveCommand = new RelayCommand(() => IsHistoryTab = false);
        ShowHistoryCommand = new RelayCommand(() => IsHistoryTab = true);

        StartStopCommand = new RelayCommand(StartStop, () => IsListening || SelectedDevice is not null);
        OpenFileCommand = new RelayCommand(BrowseFile);
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        DismissAlertCommand = new RelayCommand(() => CurrentAlert = null, () => CurrentAlert is not null);
        OpenRecordingsCommand = new RelayCommand(() => AppPaths.OpenFolder(AppPaths.RecordingsFolder));
        OpenDataFolderCommand = new RelayCommand(() => AppPaths.OpenFolder(AppPaths.DataFolder));
        CopyLogCommand = new RelayCommand(CopyLog, () => Log.Count > 0);
        ClearLogCommand = new RelayCommand(() => Log.Clear(), () => Log.Count > 0);

        _current = History.FirstOrDefault();
        _currentAt = DateTime.MinValue;
    }

    public AlertItem? CurrentAlert
    {
        get => _current;
        private set
        {
            if (!Set(ref _current, value)) return;
            RaiseCurrent();
        }
    }

    public bool HasCurrentAlert => _current is not null;
    public bool HasActiveAlert => _current is { IsExpired: false };
    public string CurrentAlertLabel => _current is null ? "" : _current.IsReceiving ? "RECEIVING ALERT · WAITING FOR REPEATS TO CONFIRM" : _current.IsExpired ? "LAST ALERT · EXPIRED" : "CURRENT ALERT";

    public string EarlierAlertsText
    {
        get
        {
            int earlier = History.Count(a => !ReferenceEquals(a, _current));
            return earlier == 0 ? "No earlier alerts" : earlier == 1 ? "1 earlier alert in History" : $"{earlier} earlier alerts in History";
        }
    }

    private void RaiseCurrent()
    {
        Raise(nameof(CurrentAlert));
        Raise(nameof(HasCurrentAlert));
        Raise(nameof(HasActiveAlert));
        Raise(nameof(CurrentAlertLabel));
        Raise(nameof(EarlierAlertsText));
        CommandManager.InvalidateRequerySuggested();
    }

    public DecoderPipeline Pipeline => _pipeline;

    public DecoderPipeline VisualPipeline => _playbackEngine.Kind != SourceKind.None ? _playbackPipeline : _pipeline;

    public ObservableCollection<AlertItem> History { get; } = [];
    public ICollectionView HistoryView { get; }
    public IReadOnlyList<SeverityFilter> HistoryFilters { get; }

    public ICommand StopPlaybackCommand { get; }
    public ICommand DeleteHistoryCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand ExportHistoryCommand { get; }
    public ICommand ShowLiveCommand { get; }
    public ICommand ShowHistoryCommand { get; }

    public bool IsHistoryTab
    {
        get => _isHistoryTab;
        set
        {
            if (!Set(ref _isHistoryTab, value)) return;
            Raise(nameof(IsLiveTab));
            if (value && SelectedHistory is null) SelectedHistory = HistoryView.Cast<AlertItem>().FirstOrDefault();
        }
    }

    public bool IsLiveTab
    {
        get => !_isHistoryTab;
        set => IsHistoryTab = !value;
    }

    public AlertItem? SelectedHistory
    {
        get => _selectedHistory;
        set { if (Set(ref _selectedHistory, value)) Raise(nameof(HasSelectedHistory)); }
    }

    public bool HasSelectedHistory => _selectedHistory is not null;

    public string HistorySearch
    {
        get => _historySearch;
        set { if (Set(ref _historySearch, value ?? "")) RefreshHistoryView(); }
    }

    public SeverityFilter SelectedHistoryFilter
    {
        get => _historyFilter;
        set { if (value is not null && Set(ref _historyFilter, value)) RefreshHistoryView(); }
    }

    public int HistoryCount => History.Count;
    public bool HasHistory => History.Count > 0;

    public string HistoryCountText
    {
        get
        {
            int shown = HistoryView.Cast<object>().Count();
            return shown == History.Count ? $"{History.Count} alert{(History.Count == 1 ? "" : "s")}" : $"{shown} of {History.Count}";
        }
    }

    public bool IsPlayingRecording => _playing is not null;
    public string PlaybackTitle => _playing is null ? "" : $"{_playing.EventName} · {_playing.ReceivedText}";
    public double PlaybackProgress { get => _playbackProgress; private set => Set(ref _playbackProgress, value); }
    public string PlaybackText { get => _playbackText; private set => Set(ref _playbackText, value); }

    public ObservableCollection<AudioDevice> Devices { get; } = [];
    public IReadOnlyList<ChannelOption> ChannelOptions { get; }
    public ObservableCollection<LogItem> Log { get; } = [];

    public event Action<AlertItem>? AlertArrived;

    public ICommand StartStopCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand DismissAlertCommand { get; }
    public ICommand OpenRecordingsCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand ClearLogCommand { get; }

    public AudioDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!Set(ref _selectedDevice, value)) return;
            if (value is not null) { _settings.DeviceId = value.Id; _settings.Save(); }
            if (!_suppressDeviceRestart && value is not null && _engine.Kind == SourceKind.Device) StartListening();
        }
    }

    public ChannelOption SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (value is null || !Set(ref _selectedChannel, value)) return;
            _pipeline.ChannelMode = value.Mode;
            _settings.ChannelMode = value.Mode.ToString();
            _settings.Save();
        }
    }

    public bool RecordAudio
    {
        get => _settings.RecordAudio;
        set
        {
            if (_settings.RecordAudio == value) return;
            _settings.RecordAudio = value;
            _pipeline.RecordingEnabled = value;
            _settings.Save();
            Raise();
            Raise(nameof(RecordingHint));
        }
    }

    public string RecordingHint => RecordAudio ? "Header to end of message, saved as WAV" : "Off — turn on in Options";

    public bool PlaySound
    {
        get => _settings.PlaySound;
        set { if (_settings.PlaySound == value) return; _settings.PlaySound = value; _settings.Save(); Raise(); }
    }

    public bool ListenOnLaunch
    {
        get => _settings.ListenOnLaunch;
        set { if (_settings.ListenOnLaunch == value) return; _settings.ListenOnLaunch = value; _settings.Save(); Raise(); }
    }

    public bool VerboseLog
    {
        get => _settings.VerboseLog;
        set { if (_settings.VerboseLog == value) return; _settings.VerboseLog = value; _settings.Save(); Raise(); }
    }

    public bool FastFileDecode
    {
        get => _settings.FastFileDecode;
        set { if (_settings.FastFileDecode == value) return; _settings.FastFileDecode = value; _settings.Save(); Raise(); }
    }

    public bool IsListening => _engine.Kind != SourceKind.None;
    public bool IsPlaybackSource => _engine.Kind is SourceKind.File;
    public bool CanChooseDevice => _engine.Kind is SourceKind.None or SourceKind.Device;
    public string StartStopText => IsListening ? "Stop" : "Start listening";
    public string StartStopGlyph => IsListening ? "" : "";

    public string StatusText => _engine.Kind switch
    {
        SourceKind.Device => $"Listening · {_engine.SourceName}",
        SourceKind.File => $"Decoding file · {_engine.SourceName}",
        _ when _pausedForPlayback is not null => $"Listening paused while a recording plays · {_pausedForPlayback.Name}",
        _ => "Idle — choose an input and press Start listening",
    };

    public string FormatText => _engine.FormatDescription;

    public bool SameActive { get => _sameActive; private set => Set(ref _sameActive, value); }
    public bool EasToneActive { get => _easTone; private set => Set(ref _easTone, value); }
    public bool NwrToneActive { get => _nwrTone; private set => Set(ref _nwrTone, value); }
    public bool RecordingActive { get => _recording; private set => Set(ref _recording, value); }
    public bool EomActive { get => _eomActive; private set => Set(ref _eomActive, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    public void Initialize(string? startupFile, bool listen, string? deviceFilter, bool fastFile)
    {
        AddLog(LogKind.Info, @"Ready. Alerts, history and recordings are saved in Documents\SAME EAS Decoder.");
        RefreshDevices();

        if (!string.IsNullOrWhiteSpace(deviceFilter))
        {
            var match = deviceFilter.Equals("default-output", StringComparison.OrdinalIgnoreCase)
                ? Devices.FirstOrDefault(d => d.Kind == AudioDeviceKind.Loopback && d.IsDefault)
                : deviceFilter.Equals("default-input", StringComparison.OrdinalIgnoreCase)
                    ? Devices.FirstOrDefault(d => d.Kind == AudioDeviceKind.Input && d.IsDefault)
                    : Devices.FirstOrDefault(d => d.Name.Contains(deviceFilter, StringComparison.CurrentCultureIgnoreCase));
            if (match is not null)
            {
                _suppressDeviceRestart = true;
                SelectedDevice = match;
                _suppressDeviceRestart = false;
            }
            else AddLog(LogKind.Error, $"No audio device matches \"{deviceFilter}\".");
        }

        if (startupFile is not null)
        {
            if (File.Exists(startupFile)) OpenFile(startupFile, fastFile || FastFileDecode);
            else AddLog(LogKind.Error, $"File not found: {startupFile}");
        }
        else if (listen && SelectedDevice is not null) StartListening();
    }

    public void Tick()
    {
        var visual = VisualPipeline;
        SameActive = visual.IsRunning && visual.IsSameCarrier;
        EasToneActive = visual.IsRunning && visual.AttentionTone == AttentionToneKind.EasDualTone;
        NwrToneActive = visual.IsRunning && visual.AttentionTone == AttentionToneKind.NwrTone;
        RecordingActive = _pipeline.IsRunning && _pipeline.IsRecording;
        EomActive = DateTime.UtcNow < _eomUntil;

        if (_playing is not null)
        {
            var pos = _playbackEngine.Position;
            var dur = _playbackEngine.Duration;
            PlaybackProgress = dur.TotalSeconds > 0 ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
            PlaybackText = $"{pos:m\\:ss} / {dur:m\\:ss}";
        }

        if (IsPlaybackSource)
        {
            var pos = _engine.Position;
            var dur = _engine.Duration;
            Progress = dur.TotalSeconds > 0 ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
            ProgressText = $"{pos:m\\:ss} / {dur:m\\:ss}";
        }

        if (DateTime.UtcNow - _lastExpiryRefresh > TimeSpan.FromSeconds(20))
        {
            _lastExpiryRefresh = DateTime.UtcNow;
            _current?.RefreshTime();
            foreach (var a in History) a.RefreshTime();
            Raise(nameof(HasActiveAlert));
            Raise(nameof(CurrentAlertLabel));
        }
    }

    public void RefreshDevices()
    {
        List<AudioDevice> devices;
        try
        {
            devices = AudioDevices.Enumerate();
        }
        catch (Exception ex)
        {
            AddLog(LogKind.Error, $"Could not list audio devices: {ex.Message}");
            return;
        }

        string? keepId = SelectedDevice?.Id ?? _settings.DeviceId;
        _suppressDeviceRestart = true;
        Devices.Clear();
        foreach (var d in devices) Devices.Add(d);
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == keepId)
            ?? Devices.FirstOrDefault(d => d.Kind == AudioDeviceKind.Input && d.IsDefault)
            ?? Devices.FirstOrDefault();
        _suppressDeviceRestart = false;
        if (Devices.Count == 0) AddLog(LogKind.Error, "No active audio devices were found.");
    }

    public void OpenFile(string path) => OpenFile(path, FastFileDecode);

    public void OpenFile(string path, bool fast)
    {
        _resumeDevice ??= _engine.Kind == SourceKind.Device ? SelectedDevice : _pausedForPlayback;
        _pausedForPlayback = null;
        try
        {
            _engine.StartFile(path, fast);
            AddLog(LogKind.Info, $"Decoding {Path.GetFileName(path)} ({_engine.FormatDescription}){(fast ? " at full speed" : "")}");
        }
        catch (Exception ex)
        {
            AddLog(LogKind.Error, $"Could not open {Path.GetFileName(path)}: {ex.Message}");
            _engine.Stop();
            ResumeLiveInput();
        }
        RaiseSourceState();
    }

    private void StartStop()
    {
        if (IsListening)
        {
            _resumeDevice = null;
            StopSource();
            AddLog(LogKind.Info, "Stopped.");
        }
        else StartListening();
    }

    private void StartListening()
    {
        if (SelectedDevice is null) return;
        _resumeDevice = null;
        if (_playing is not null)
        {
            _pausedForPlayback = null;
            StopPlayback(resumeListening: false);
        }
        try
        {
            _engine.StartDevice(SelectedDevice);
            AddLog(LogKind.Success, $"Listening on {SelectedDevice.Name} ({_engine.FormatDescription})");
        }
        catch (Exception ex)
        {
            _engine.Stop();
            AddLog(LogKind.Error, $"Could not open {SelectedDevice.Name}: {ex.Message}");
        }
        RaiseSourceState();
    }

    private void StopSource()
    {
        _engine.Stop();
        Progress = 0;
        ProgressText = "";
        RaiseSourceState();
    }

    private void BrowseFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Decode an audio recording",
            Filter = "Audio files|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac;*.aiff;*.aif|All files|*.*",
        };
        if (dialog.ShowDialog() == true) OpenFile(dialog.FileName);
    }

    private void OnSourceCompleted()
    {
        if (_engine.Kind == SourceKind.None) return;
        string name = _engine.SourceName;
        StopSource();
        AddLog(LogKind.Info, $"Finished decoding {name}.");
        ResumeLiveInput();
    }

    private void OnSourceFailed(string message)
    {
        AddLog(LogKind.Error, $"Audio input stopped: {message}");
        StopSource();
        RefreshDevices();
    }

    private void ResumeLiveInput()
    {
        var device = _resumeDevice;
        _resumeDevice = null;
        if (device is null) return;
        var match = Devices.FirstOrDefault(d => d.Id == device.Id);
        if (match is null) return;
        _suppressDeviceRestart = true;
        SelectedDevice = match;
        _suppressDeviceRestart = false;
        StartListening();
    }

    private void OnBurst(SameBurst b)
    {
        bool sameGroup = _lastBurstEnd >= 0 && b.Kind == _lastBurstKind &&
                         b.StartSample - _lastBurstEnd < DecoderPipeline.DecodeRate * 3.5 && b.StartSample >= _lastBurstEnd;
        _burstNumber = sameGroup ? _burstNumber + 1 : 1;
        _lastBurstEnd = b.EndSample;
        _lastBurstKind = b.Kind;

        if (b.Kind == SameBurstKind.Header && _burstNumber == 1 && _activationItem is not null) CloseActivation();

        var kind = b.ErrorCount > 0 ? LogKind.Warning : LogKind.Data;
        bool activation = b.Kind != SameBurstKind.Unknown || _activationLog is not null;
        AddLog(kind, VerboseFormat.BurstSummary(b, _burstNumber), VerboseFormat.BurstLines(b), activation);
    }

    private void OnHeaderRejected(string voted, IReadOnlyList<string> bursts)
    {
        AddLog(LogKind.Error, VerboseFormat.VoteSummary(SameVoting.Vote(bursts), accepted: false) + " — not a valid SAME header",
            VerboseFormat.VoteLines(voted, bursts), activation: true);
        if (_receiving is { } shown)
        {
            _receiving = null;
            shown.Apply(shown.Source, receiving: false);
            RaiseCurrent();
            AddLog(LogKind.Warning, $"Keeping {shown.EventName} as read from the earlier burst(s) — unconfirmed", activation: true);
            KeepInHistory(shown);
            return;
        }
        CloseActivation();
    }

    private void CloseActivation()
    {
        if (_activationItem is not null && History.Contains(_activationItem)) ScheduleHistorySave();
        _activationItem = null;
        _activationLog = null;
    }

    private string CurrentOrigin => _engine.Kind switch
    {
        SourceKind.File => $"File · {_engine.SourceName}",
        _ => _engine.SourceName,
    };

    private AlertItem ShowNewAlert(SameAlert alert, bool receiving)
    {
        var item = new AlertItem(alert, CurrentOrigin, receiving);
        _currentAt = DateTime.UtcNow;
        _activationLog ??= [];
        _activationItem = item;
        item.AttachActivityLog(_activationLog);
        CurrentAlert = item;
        IsHistoryTab = false;
        if (PlaySound)
        {
            if (alert.Header.Event.Severity is SameSeverity.Emergency or SameSeverity.Warning) SystemSounds.Hand.Play();
            else SystemSounds.Asterisk.Play();
        }
        AlertArrived?.Invoke(item);
        return item;
    }

    private void OnAlertProgress(SameAlert alert)
    {
        int n = alert.Vote.BurstCount;
        if (_receiving is null)
        {
            _receiving = ShowNewAlert(alert, receiving: true);
            AddLog(LogKind.Alert, $"Showing {_receiving.EventName} ({_receiving.EventCode}) from burst {n} — waiting for the repeats to confirm it",
                VerboseFormat.HeaderLines(alert.Header), activation: true);
            return;
        }

        var changes = VerboseFormat.HeaderChangeLines(_receiving.Source.Header, alert.Header);
        _receiving.Apply(alert, receiving: true);
        RaiseCurrent();

        var voteLines = VerboseFormat.VoteLines(alert.Vote.Text, alert.RawBursts);
        int differing = CountDifferences(alert.RawBursts);
        if (changes.Count > 0)
        {
            AddLog(LogKind.Warning, $"Burst {n} corrected the alert on screen ({changes.Count} field{(changes.Count == 1 ? "" : "s")})",
                [.. changes, .. voteLines], activation: true);
        }
        else if (differing == 0)
        {
            AddLog(LogKind.Success, $"Burst {n} matches burst 1 exactly — alert on screen unchanged", voteLines, activation: true);
        }
        else
        {
            AddLog(LogKind.Warning, $"Burst {n} differs from burst 1 in {differing} character{(differing == 1 ? "" : "s")} — no majority yet, burst 3 will decide",
                voteLines, activation: true);
        }
    }

    private void OnAlert(SameAlert alert)
    {
        AlertItem item;
        if (_receiving is not null)
        {
            item = _receiving;
            _receiving = null;
            var changes = VerboseFormat.HeaderChangeLines(item.Source.Header, alert.Header);
            item.Apply(alert, receiving: false);
            RaiseCurrent();
            AddLog(alert.Verified ? LogKind.Success : LogKind.Warning, VerboseFormat.VoteSummary(alert.Vote, accepted: true),
                VerboseFormat.VoteLines(alert.Vote.Text, alert.RawBursts), activation: true);
            if (changes.Count > 0)
                AddLog(LogKind.Warning, $"Final vote corrected {changes.Count} field{(changes.Count == 1 ? "" : "s")} shown from the earlier bursts", changes, activation: true);
            AddLog(LogKind.Alert, $"Confirmed {item.EventName} ({item.EventCode}) from {item.OriginLine}",
                VerboseFormat.HeaderLines(alert.Header), activation: true);
        }
        else
        {
            AddLog(alert.Verified ? LogKind.Success : LogKind.Warning, VerboseFormat.VoteSummary(alert.Vote, accepted: true),
                VerboseFormat.VoteLines(alert.Vote.Text, alert.RawBursts), activation: true);
            item = ShowNewAlert(alert, receiving: false);
            AddLog(LogKind.Alert, $"Decoded {item.EventName} ({item.EventCode}) from {item.OriginLine}",
                VerboseFormat.HeaderLines(alert.Header), activation: true);
        }
        KeepInHistory(item);
    }

    private static int CountDifferences(IReadOnlyList<string> bursts)
    {
        if (bursts.Count < 2) return 0;
        string a = bursts[0], b = bursts[1];
        int diff = Math.Abs(a.Length - b.Length);
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) diff++;
        return diff;
    }

    private void KeepInHistory(AlertItem item)
    {
        if (History.Contains(item)) return;
        AppendToAlertLog(item);
        History.Insert(0, item);
        item.PropertyChanged += OnHistoryItemChanged;
        ScheduleHistorySave();
    }

    private void OnEndOfMessage(SameEndOfMessage e)
    {
        _eomUntil = DateTime.UtcNow.AddSeconds(4);
        AddLog(LogKind.Success, $"End of message (NNNN × {e.BurstCount})", activation: _activationLog is not null);
        if (_current is not null && DateTime.UtcNow - _currentAt < TimeSpan.FromMinutes(10)) _current.EomReceived = true;
    }

    private void OnToneEnded(AttentionToneKind kind, TimeSpan duration)
    {
        AddLog(LogKind.Tone, $"{ToneName(kind)} ended after {duration.TotalSeconds:0.0} s", activation: _activationLog is not null);
        if (_current is not null && _current.ToneText is null && DateTime.UtcNow - _currentAt < TimeSpan.FromMinutes(2))
            _current.ToneText = $"{(kind == AttentionToneKind.EasDualTone ? "853 + 960 Hz" : "1050 Hz")} · {duration.TotalSeconds:0.0} s";
    }

    private void OnRecordingSaved(AlertRecording r)
    {
        var item = ReferenceEquals(_current?.Source, r.Alert) ? _current : History.FirstOrDefault(a => ReferenceEquals(a.Source, r.Alert));
        if (item is not null) item.RecordingPath = r.Path;
        var entry = AddLog(LogKind.Success, $"Saved recording {Path.GetFileName(r.Path)} ({r.Duration.TotalSeconds:0} s)");
        if (item is not null)
        {
            if (!item.ActivityLog.Contains(entry)) item.ActivityLog.Add(entry);
            if (ReferenceEquals(item, _activationItem)) CloseActivation();
            else if (History.Contains(item)) ScheduleHistorySave();
        }
    }

    private void TogglePlayback(AlertItem item)
    {
        if (ReferenceEquals(_playing, item))
        {
            StopPlayback();
            return;
        }
        StopPlayback(resumeListening: false);
        if (item.RecordingPath is null || !File.Exists(item.RecordingPath))
        {
            ResumeAfterPlayback();
            return;
        }
        if (_engine.Kind == SourceKind.Device)
        {
            _pausedForPlayback = SelectedDevice;
            StopSource();
            AddLog(LogKind.Info, "Listening paused while the recording plays, so the playback is not decoded as a new alert.");
        }
        try
        {
            _playbackEngine.StartFile(item.RecordingPath, fast: false);
            _playing = item;
            item.IsPlaying = true;
            PlaybackProgress = 0;
            PlaybackText = "";
            AddLog(LogKind.Info, $"Playing recording of {item.EventName} ({Path.GetFileName(item.RecordingPath)})");
        }
        catch (Exception ex)
        {
            _playbackEngine.Stop();
            AddLog(LogKind.Error, $"Could not play recording: {ex.Message}");
            ResumeAfterPlayback();
        }
        RaisePlayback();
    }

    private void StopPlayback() => StopPlayback(resumeListening: true);

    private void StopPlayback(bool resumeListening)
    {
        _playbackEngine.Stop();
        if (_playing is not null) _playing.IsPlaying = false;
        _playing = null;
        RaisePlayback();
        if (resumeListening) ResumeAfterPlayback();
    }

    private void ResumeAfterPlayback()
    {
        var device = _pausedForPlayback;
        _pausedForPlayback = null;
        if (device is null || _engine.Kind != SourceKind.None) { RaiseSourceState(); return; }
        var match = Devices.FirstOrDefault(d => d.Id == device.Id);
        if (match is null) { RaiseSourceState(); return; }
        _suppressDeviceRestart = true;
        SelectedDevice = match;
        _suppressDeviceRestart = false;
        StartListening();
        AddLog(LogKind.Info, "Playback finished — listening resumed.");
    }

    private void RaisePlayback()
    {
        Raise(nameof(IsPlayingRecording));
        Raise(nameof(PlaybackTitle));
        Raise(nameof(VisualPipeline));
        Raise(nameof(StatusText));
        CommandManager.InvalidateRequerySuggested();
    }

    private void LoadHistory()
    {
        foreach (var entry in AlertHistory.Load().AsEnumerable().Reverse())
        {
            if (AlertItem.FromHistory(entry) is not { } item) continue;
            item.PropertyChanged += OnHistoryItemChanged;
            History.Add(item);
        }
    }

    private void OnHistoryItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AlertItem.ToneText) or nameof(AlertItem.EomReceived) or nameof(AlertItem.RecordingPath) or nameof(AlertItem.ActivityLog))
            ScheduleHistorySave();
    }

    private void ScheduleHistorySave()
    {
        _historySaveTimer.Stop();
        _historySaveTimer.Start();
    }

    private void SaveHistoryNow()
    {
        _historySaveTimer.Stop();
        try
        {
            AlertHistory.Save(History.Reverse().Select(h => h.ToHistory()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog(LogKind.Error, $"Could not save alert history: {ex.Message}");
        }
    }

    private bool MatchesSearch(AlertItem a)
    {
        string q = _historySearch.Trim();
        if (q.Length == 0) return true;
        return a.EventName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || a.EventCode.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.OriginLine.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || a.RawHeader.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.AreaNames.Any(n => n.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    private void RefreshHistoryView()
    {
        HistoryView.Refresh();
        if (SelectedHistory is not null && !HistoryView.Contains(SelectedHistory))
            SelectedHistory = HistoryView.Cast<AlertItem>().FirstOrDefault();
        RaiseHistoryCounts();
    }

    private void RaiseHistoryCounts()
    {
        Raise(nameof(HistoryCount));
        Raise(nameof(HasHistory));
        Raise(nameof(HistoryCountText));
        Raise(nameof(EarlierAlertsText));
    }

    private void DeleteHistory(AlertItem? item)
    {
        if (item is null) return;
        if (ReferenceEquals(_playing, item)) StopPlayback();
        int index = HistoryView.Cast<AlertItem>().ToList().IndexOf(item);
        History.Remove(item);
        item.PropertyChanged -= OnHistoryItemChanged;
        if (ReferenceEquals(_current, item)) CurrentAlert = History.FirstOrDefault();
        var remaining = HistoryView.Cast<AlertItem>().ToList();
        SelectedHistory = remaining.Count == 0 ? null : remaining[Math.Clamp(index, 0, remaining.Count - 1)];
        SaveHistoryNow();
    }

    private void ClearHistory()
    {
        var answer = MessageBox.Show(Application.Current.MainWindow,
            $"Remove all {History.Count} alerts from History?\n\nRecordings and the alerts.log file are kept.",
            "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        StopPlayback();
        foreach (var item in History) item.PropertyChanged -= OnHistoryItemChanged;
        if (_current is not null && History.Contains(_current)) CurrentAlert = null;
        History.Clear();
        SelectedHistory = null;
        SaveHistoryNow();
        AddLog(LogKind.Info, "Alert history cleared.");
    }

    private void ExportHistory()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export alert history",
            Filter = "CSV file|*.csv",
            FileName = $"SAME alerts {DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            AlertHistory.ExportCsv(dialog.FileName, HistoryView.Cast<AlertItem>());
            AddLog(LogKind.Success, $"Exported {HistoryView.Cast<object>().Count()} alerts to {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog(LogKind.Error, $"Export failed: {ex.Message}");
        }
    }

    private void CopyLog()
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, Log.Reverse().Select(l => l.ToString()))); }
        catch (Exception) { }
    }

    private void AppendToAlertLog(AlertItem item)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataFolder);
            File.AppendAllText(AppPaths.AlertLogFile, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{item.ToReport()}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddLog(LogKind.Error, $"Could not write alert log: {ex.Message}");
        }
    }

    private LogItem AddLog(LogKind kind, string message, IReadOnlyList<IReadOnlyList<LogSegment>>? lines = null, bool activation = false)
    {
        var entry = new LogItem(DateTime.Now, kind, message, lines);
        Log.Insert(0, entry);
        while (Log.Count > MaxLogItems) Log.RemoveAt(Log.Count - 1);
        if (activation)
        {
            _activationLog ??= [];
            _activationLog.Add(entry);
        }
        return entry;
    }

    private void RaiseSourceState()
    {
        Raise(nameof(IsListening));
        Raise(nameof(IsPlaybackSource));
        Raise(nameof(CanChooseDevice));
        Raise(nameof(StartStopText));
        Raise(nameof(StartStopGlyph));
        Raise(nameof(StatusText));
        Raise(nameof(FormatText));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string ToneName(AttentionToneKind k) => k == AttentionToneKind.EasDualTone
        ? "EAS attention signal (853 + 960 Hz)"
        : "NWR alert tone (1050 Hz)";

    private void Post(Action action) => _ui.BeginInvoke(action);

    public void Dispose()
    {
        _playbackEngine.Dispose();
        _engine.Dispose();
        if (_historySaveTimer.IsEnabled) SaveHistoryNow();
        _settings.Save();
    }
}
