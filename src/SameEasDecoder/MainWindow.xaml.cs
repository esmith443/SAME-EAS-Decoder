using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SameEasDecoder.Controls;
using SameEasDecoder.Services;
using SameEasDecoder.ViewModels;

namespace SameEasDecoder;

public partial class MainWindow : Window
{
    private sealed class MeterState
    {
        public double Peak = LevelMeter.MinDb, Rms = LevelMeter.MinDb, Hold = LevelMeter.MinDb;
        public DateTime HoldAt;
    }

    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly MeterState _left = new(), _right = new();
    private long _lastSeq = -1;
    private DateTime _lastSeqAt, _lastFrame = DateTime.UtcNow, _clipUntil;
    private int _frame, _shownChannels = 2;

    public MainWindow(MainViewModel vm, AppSettings settings)
    {
        InitializeComponent();
        _vm = vm;
        _settings = settings;
        DataContext = vm;

        if (settings.WindowWidth is > 0 && settings.WindowHeight is > 0)
        {
            Width = Math.Min(settings.WindowWidth.Value, SystemParameters.WorkArea.Width);
            Height = Math.Min(settings.WindowHeight.Value, SystemParameters.WorkArea.Height);
        }
        if (settings.Maximized) WindowState = WindowState.Maximized;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnFrame;

        SourceInitialized += (_, _) => NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => _timer.Start();
        Closing += OnClosing;
        DragOver += OnDragOver;
        Drop += OnDrop;
        vm.AlertArrived += _ =>
        {
            if (!IsActive) NativeMethods.FlashTaskbar(new WindowInteropHelper(this).Handle);
        };

        LiveRoot.SizeChanged += (_, _) => AlertsScroll.MaxHeight = Math.Max(180, LiveRoot.ActualHeight - 34 - 2 - 180);

        LogList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) =>
        {
            var lines = LogList.SelectedItems.Cast<LogItem>().OrderBy(l => l.Time).Select(l => l.ToString());
            try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); } catch (Exception) { }
        }));
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.25);
        _lastFrame = now;

        var pipeline = _vm.VisualPipeline;
        var levels = pipeline.Levels;
        if (levels.Sequence != _lastSeq)
        {
            _lastSeq = levels.Sequence;
            _lastSeqAt = now;
        }
        bool stale = !pipeline.IsRunning || (now - _lastSeqAt).TotalMilliseconds > 300;
        int channels = Math.Max(1, levels.Peak.Length);

        if (channels != _shownChannels)
        {
            _shownChannels = channels;
            var vis = channels >= 2 ? Visibility.Visible : Visibility.Collapsed;
            LabelR.Visibility = MeterR.Visibility = ReadoutR.Visibility = vis;
            LabelL.Text = channels >= 2 ? "L" : "M";
        }

        float pl = stale ? 0 : levels.Peak[0], rl = stale ? 0 : levels.Rms[0];
        UpdateMeter(_left, MeterL, ReadoutL, pl, rl, dt, now);
        float maxPeak = pl;
        if (channels >= 2)
        {
            float pr = stale ? 0 : levels.Peak[1], rr = stale ? 0 : levels.Rms[1];
            UpdateMeter(_right, MeterR, ReadoutR, pr, rr, dt, now);
            maxPeak = Math.Max(maxPeak, pr);
        }
        if (maxPeak >= 0.999f) _clipUntil = now.AddSeconds(2);
        ClipBadge.Visibility = now < _clipUntil ? Visibility.Visible : Visibility.Hidden;

        bool any = false;
        while (pipeline.TryDequeueSpectrum(out var frame))
        {
            Waterfall.AddRow(frame);
            any = true;
        }
        if (any) Waterfall.Commit();

        if (++_frame % 3 == 0) _vm.Tick();
    }

    private static void UpdateMeter(MeterState s, LevelMeter meter, TextBlock readout, float peak, float rms, double dt, DateTime now)
    {
        double p = ToDb(peak), r = ToDb(rms);
        s.Peak = p >= s.Peak ? p : Math.Max(p, s.Peak - 30 * dt);
        s.Rms = r >= s.Rms ? s.Rms + (r - s.Rms) * 0.5 : Math.Max(r, s.Rms - 20 * dt);
        if (p >= s.Hold) { s.Hold = p; s.HoldAt = now; }
        else if ((now - s.HoldAt).TotalSeconds > 1.5) s.Hold = Math.Max(p, s.Hold - 15 * dt);

        meter.PeakDb = s.Peak;
        meter.RmsDb = s.Rms;
        meter.HoldDb = s.Hold;
        string text = s.Hold <= LevelMeter.MinDb + 0.5 ? "−∞" : s.Hold.ToString("0.0");
        if (readout.Text != text) readout.Text = text;
    }

    private static double ToDb(float linear) => linear <= 1e-6f ? -120 : 20 * Math.Log10(linear);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) _vm.OpenFile(files[0]);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.Maximized = WindowState == WindowState.Maximized;
        _vm.Dispose();
    }
}
