using System.Windows;
using System.Windows.Media;

namespace SameEasDecoder.Controls;

public sealed class LevelMeter : FrameworkElement
{
    public const double MinDb = -60;

    public static readonly DependencyProperty RmsDbProperty = DependencyProperty.Register(
        nameof(RmsDb), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(MinDb, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakDbProperty = DependencyProperty.Register(
        nameof(PeakDb), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(MinDb, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoldDbProperty = DependencyProperty.Register(
        nameof(HoldDb), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(MinDb, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1A)));
    private static readonly Brush Off = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2C)));
    private static readonly Color Green = Color.FromRgb(0x3D, 0xD6, 0x8C);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xB2, 0x24);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x5F, 0x57);
    private static readonly Brush HoldBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF0)));

    public double RmsDb { get => (double)GetValue(RmsDbProperty); set => SetValue(RmsDbProperty, value); }
    public double PeakDb { get => (double)GetValue(PeakDbProperty); set => SetValue(PeakDbProperty, value); }
    public double HoldDb { get => (double)GetValue(HoldDbProperty); set => SetValue(HoldDbProperty, value); }

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    private static readonly Brush[] Lit = [Solid(Green, 0xFF), Solid(Amber, 0xFF), Solid(Red, 0xFF)];
    private static readonly Brush[] Dim = [Solid(Green, 0x70), Solid(Amber, 0x70), Solid(Red, 0x70)];

    private static Brush Solid(Color c, byte a) => Freeze(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)));

    private static int Zone(double db) => db >= -6 ? 2 : db >= -18 ? 1 : 0;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), 3, 3);

        const double seg = 4, gap = 2;
        int count = (int)((w - 4 + gap) / (seg + gap));
        double x0 = 2, y = 2, sh = h - 4;
        double rmsX = X(RmsDb, w), peakX = X(PeakDb, w);

        for (int i = 0; i < count; i++)
        {
            double x = x0 + i * (seg + gap);
            double center = x + seg / 2;
            double db = MinDb + (center - 2) / (w - 4) * -MinDb;
            Brush brush = center <= rmsX ? Lit[Zone(db)] : center <= peakX ? Dim[Zone(db)] : Off;
            dc.DrawRectangle(brush, null, new Rect(x, y, seg, sh));
        }

        if (HoldDb > MinDb + 0.5)
        {
            double hx = Math.Min(w - 3, X(HoldDb, w));
            dc.DrawRectangle(HoldDb >= -1 ? Lit[2] : HoldBrush, null, new Rect(hx - 1, 1, 2, h - 2));
        }
    }

    private static double X(double db, double w) => 2 + Math.Clamp((db - MinDb) / -MinDb, 0, 1) * (w - 4);
}
