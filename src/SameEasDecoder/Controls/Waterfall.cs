using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SameEasDecoder.Controls;

public sealed class Waterfall : FrameworkElement
{
    private readonly int[] _lut = BuildColorMap();
    private WriteableBitmap? _bitmap;
    private int[] _pixels = [];
    private int _width, _rows, _wantedRows = 160;
    private double _floor = -100;
    private bool _dirty;

    private static readonly Pen EasPen = MakePen(Color.FromArgb(0xA0, 0xFF, 0xB2, 0x24));
    private static readonly Pen SamePen = MakePen(Color.FromArgb(0xA0, 0x4D, 0xA3, 0xFF));
    private static readonly Brush LabelBg = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0x0E, 0x10, 0x14)));
    private static readonly Brush EasText = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x24)));
    private static readonly Brush SameText = Frozen(new SolidColorBrush(Color.FromRgb(0x4D, 0xA3, 0xFF)));
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x11)));

    public Waterfall()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
    }

    public double MaxFrequency { get; set; } = 3000;

    public double BinWidth { get; set; } = 16000.0 / 1024;

    public void Clear()
    {
        if (_pixels.Length > 0) Array.Fill(_pixels, _lut[0]);
        _dirty = true;
        Commit();
    }

    public void AddRow(float[] db)
    {
        int bins = Math.Min(db.Length, (int)(MaxFrequency / BinWidth));
        if (bins <= 0) return;
        if (_bitmap is null || _width != bins || _rows != _wantedRows)
        {
            int rows = _wantedRows;
            var pixels = new int[bins * rows];
            Array.Fill(pixels, _lut[0]);
            if (_width == bins && _pixels.Length > 0)
                Array.Copy(_pixels, pixels, Math.Min(_pixels.Length, pixels.Length));
            _width = bins;
            _rows = rows;
            _pixels = pixels;
            _bitmap = new WriteableBitmap(bins, rows, 96, 96, PixelFormats.Bgr32, null);
        }

        var sorted = db.AsSpan(0, bins).ToArray();
        Array.Sort(sorted);
        double p25 = Math.Max(-130, sorted[bins / 4]);
        _floor += (p25 - _floor) * (p25 < _floor ? 0.2 : 0.03);

        Array.Copy(_pixels, 0, _pixels, bins, bins * (_rows - 1));
        double lo = _floor + 4, range = 56;
        for (int i = 0; i < bins; i++)
        {
            double v = (db[i] - lo) / range;
            int idx = (int)(Math.Clamp(v, 0, 1) * 255);
            _pixels[i] = _lut[idx];
        }
        _dirty = true;
    }

    public void Commit()
    {
        if (!_dirty || _bitmap is null) return;
        _bitmap.WritePixels(new Int32Rect(0, 0, _width, _rows), _pixels, _width * 4, 0);
        _dirty = false;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        _wantedRows = Math.Clamp((int)Math.Round(sizeInfo.NewSize.Height * scale), 60, 1200);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 6, 6));
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        if (_bitmap is not null) dc.DrawImage(_bitmap, new Rect(0, 0, w, h));

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (double f in new[] { 853.0, 960.0, 1050.0 }) Marker(dc, f, EasPen, w, h);
        foreach (double f in new[] { 1562.5, 2083.3 }) Marker(dc, f, SamePen, w, h);
        Label(dc, "EAS / NWR", (853 + 1050) / 2.0, EasText, w, dpi);
        Label(dc, "SAME", (1562.5 + 2083.3) / 2.0, SameText, w, dpi);
        dc.Pop();
    }

    private void Marker(DrawingContext dc, double f, Pen pen, double w, double h)
    {
        double x = Math.Round(f / MaxFrequency * w) + 0.5;
        dc.DrawLine(pen, new Point(x, 18), new Point(x, h));
    }

    private void Label(DrawingContext dc, string text, double f, Brush brush, double w, double dpi)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 10, brush, dpi);
        double x = Math.Clamp(f / MaxFrequency * w - ft.Width / 2, 2, w - ft.Width - 2);
        dc.DrawRoundedRectangle(LabelBg, null, new Rect(x - 4, 3, ft.Width + 8, ft.Height + 2), 3, 3);
        dc.DrawText(ft, new Point(x, 4));
    }

    private static Pen MakePen(Color c)
    {
        var pen = new Pen(new SolidColorBrush(c), 1) { DashStyle = new DashStyle([3, 3], 0) };
        pen.Freeze();
        return pen;
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static int[] BuildColorMap()
    {
        (double Pos, Color C)[] stops =
        [
            (0.00, Color.FromRgb(0x0B, 0x0D, 0x11)),
            (0.25, Color.FromRgb(0x10, 0x2A, 0x55)),
            (0.50, Color.FromRgb(0x1D, 0x6F, 0xD6)),
            (0.72, Color.FromRgb(0x2F, 0xC8, 0xE6)),
            (0.88, Color.FromRgb(0xF6, 0xE0, 0x6A)),
            (1.00, Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ];
        var lut = new int[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            int k = 0;
            while (k < stops.Length - 2 && t > stops[k + 1].Pos) k++;
            var (p0, c0) = stops[k];
            var (p1, c1) = stops[k + 1];
            double u = (t - p0) / (p1 - p0);
            byte r = (byte)(c0.R + (c1.R - c0.R) * u);
            byte g = (byte)(c0.G + (c1.G - c0.G) * u);
            byte b = (byte)(c0.B + (c1.B - c0.B) * u);
            lut[i] = (r << 16) | (g << 8) | b;
        }
        return lut;
    }
}
