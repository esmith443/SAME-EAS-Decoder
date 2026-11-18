using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SameEasDecoder.Core.Geo;
using SameEasDecoder.Core.Same;

namespace SameEasDecoder.Controls;

public sealed class AlertMap : FrameworkElement
{
    public static readonly DependencyProperty AreasProperty = DependencyProperty.Register(
        nameof(Areas), typeof(IReadOnlyList<SameLocation>), typeof(AlertMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((AlertMap)d).Rebuild()));

    public static readonly DependencyProperty HighlightColorProperty = DependencyProperty.Register(
        nameof(HighlightColor), typeof(Color), typeof(AlertMap),
        new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x5F, 0x57), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
        nameof(Interactive), typeof(bool), typeof(AlertMap),
        new FrameworkPropertyMetadata(false, (d, _) => ((AlertMap)d).Rebuild()));

    public static readonly DependencyProperty ExpandCommandProperty = DependencyProperty.Register(
        nameof(ExpandCommand), typeof(ICommand), typeof(AlertMap), new PropertyMetadata(null));

    private static readonly Brush Water = Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x0E, 0x13)));
    private static readonly Brush Land = Frozen(new SolidColorBrush(Color.FromRgb(0x18, 0x1C, 0x24)));
    private static readonly Color CountyLine = Color.FromRgb(0x2C, 0x32, 0x3E);
    private static readonly Color StateLine = Color.FromRgb(0x5A, 0x63, 0x75);
    private static readonly Brush LabelText = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0xEB, 0xF0)));
    private static readonly Brush StateText = Frozen(new SolidColorBrush(Color.FromRgb(0x7A, 0x83, 0x94)));
    private static readonly Brush HintText = Frozen(new SolidColorBrush(Color.FromRgb(0xA3, 0xAB, 0xB8)));
    private static readonly Brush ChipBg = Frozen(new SolidColorBrush(Color.FromArgb(0xD8, 0x0E, 0x10, 0x14)));
    private static readonly Pen Halo = FrozenPen(Color.FromArgb(0xE0, 0x0B, 0x0E, 0x13), 3);
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface FaceRegular = new("Segoe UI");

    private MapScene? _scene;
    private Matrix _user = Matrix.Identity;
    private Point _dragStart;
    private Matrix _dragUser;
    private bool _dragging;
    private MapRegion? _hover;

    public AlertMap()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
    }

    public IReadOnlyList<SameLocation>? Areas { get => (IReadOnlyList<SameLocation>?)GetValue(AreasProperty); set => SetValue(AreasProperty, value); }
    public Color HighlightColor { get => (Color)GetValue(HighlightColorProperty); set => SetValue(HighlightColorProperty, value); }
    public bool Interactive { get => (bool)GetValue(InteractiveProperty); set => SetValue(InteractiveProperty, value); }
    public ICommand? ExpandCommand { get => (ICommand?)GetValue(ExpandCommandProperty); set => SetValue(ExpandCommandProperty, value); }

    private void Rebuild()
    {
        _scene = Areas is { Count: > 0 } areas ? MapScene.Build(areas, Interactive) : null;
        _user = Matrix.Identity;
        _hover = null;
        Cursor = Interactive ? Cursors.SizeAll : Cursors.Hand;
        InvalidateVisual();
    }

    private Matrix ViewMatrix()
    {
        var m = Matrix.Identity;
        if (_scene is null || ActualWidth <= 0 || ActualHeight <= 0) return m;
        var f = _scene.Focus;
        double grow = Interactive ? 0.12 : 0.3;
        f.Inflate(f.Width * grow, f.Height * grow);
        double pad = Interactive ? 48 : 12;
        double s = Math.Min((ActualWidth - 2 * pad) / f.Width, (ActualHeight - 2 * pad) / f.Height);
        if (double.IsNaN(s) || s <= 0) s = 1;
        m.Translate(-(f.Left + f.Width / 2), -(f.Top + f.Height / 2));
        m.Scale(s, s);
        m.Translate(ActualWidth / 2, ActualHeight / 2);
        m.Append(_user);
        return m;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        dc.PushClip(new RectangleGeometry(bounds, CornerRadius, CornerRadius));
        dc.DrawRectangle(Water, null, bounds);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_scene is null)
        {
            string msg = Areas is { Count: > 0 } ? "These areas can't be shown on the map" : "";
            if (UsAtlas.Instance is null) msg = "Map data unavailable";
            var ft = Text(msg, 12, HintText, dpi, FaceRegular);
            dc.DrawText(ft, new Point((bounds.Width - ft.Width) / 2, (bounds.Height - ft.Height) / 2));
            dc.Pop();
            return;
        }

        var m = ViewMatrix();
        double scale = Math.Sqrt(Math.Abs(m.Determinant));
        double px = 1 / scale;
        var inverse = m;
        inverse.Invert();
        var visible = new Rect(inverse.Transform(new Point(0, 0)), inverse.Transform(new Point(bounds.Width, bounds.Height)));

        var accent = HighlightColor;
        var fillStrong = new SolidColorBrush(Color.FromArgb(0x8C, accent.R, accent.G, accent.B));
        var fillLight = new SolidColorBrush(Color.FromArgb(0x30, accent.R, accent.G, accent.B));
        var outline = new Pen(new SolidColorBrush(accent), 1.6 * px) { LineJoin = PenLineJoin.Round };
        var countyPen = new Pen(new SolidColorBrush(CountyLine), 0.8 * px);
        var statePen = new Pen(new SolidColorBrush(StateLine), 1.3 * px) { LineJoin = PenLineJoin.Round };
        var hoverPen = new Pen(Brushes.White, 1.8 * px) { LineJoin = PenLineJoin.Round };

        dc.PushTransform(new MatrixTransform(m));
        foreach (var r in _scene.Context)
            if (r.Bounds.IntersectsWith(visible)) dc.DrawGeometry(Land, countyPen, r.Geometry);
        foreach (var s in _scene.States)
            if (s.Bounds.IntersectsWith(visible)) dc.DrawGeometry(_scene.Context.Count == 0 ? Land : null, null, s.Geometry);
        foreach (var a in _scene.Affected)
        {
            if (a.Part is null) dc.DrawGeometry(fillStrong, null, a.Geometry);
            else
            {
                dc.DrawGeometry(fillLight, null, a.Geometry);
                dc.DrawGeometry(fillStrong, null, a.Part);
            }
        }
        foreach (var s in _scene.States)
            if (s.Bounds.IntersectsWith(visible)) dc.DrawGeometry(null, statePen, s.Geometry);
        foreach (var a in _scene.Affected) dc.DrawGeometry(null, outline, a.Part ?? a.Geometry);
        if (_hover is not null) dc.DrawGeometry(null, hoverPen, _hover.Geometry);
        dc.Pop();

        DrawLabels(dc, m, bounds, dpi);
        DrawFooter(dc, bounds, dpi);
        dc.Pop();
    }

    public double CornerRadius { get; set; } = 7;

    private void DrawLabels(DrawingContext dc, Matrix m, Rect bounds, double dpi)
    {
        var placed = new List<Rect>();

        foreach (var a in _scene!.Affected)
        {
            var screen = Rect.Transform(a.Bounds, m);
            var ft = Text(a.ShortName, 11.5, LabelText, dpi, Face);
            if (screen.Width < ft.Width * 0.7 && _scene.Affected.Count > 3) continue;
            TryPlace(dc, ft, m.Transform(a.LabelPoint), bounds, placed, halo: true);
        }
        foreach (var s in _scene.States)
        {
            var screen = Rect.Transform(s.Bounds, m);
            if (screen.Width < 40 || screen.Height < 24) continue;
            var ft = Text(s.ShortName, 10.5, StateText, dpi, Face);
            TryPlace(dc, ft, m.Transform(s.LabelPoint), bounds, placed, halo: true);
        }
    }

    private static void TryPlace(DrawingContext dc, FormattedText ft, Point center, Rect bounds, List<Rect> placed, bool halo)
    {
        var rect = new Rect(center.X - ft.Width / 2, center.Y - ft.Height / 2, ft.Width, ft.Height);
        if (!bounds.Contains(rect)) return;
        var padded = rect;
        padded.Inflate(3, 1);
        if (placed.Any(p => p.IntersectsWith(padded))) return;
        placed.Add(padded);
        if (halo) dc.DrawGeometry(null, Halo, ft.BuildGeometry(rect.TopLeft));
        dc.DrawText(ft, rect.TopLeft);
    }

    private void DrawFooter(DrawingContext dc, Rect bounds, double dpi)
    {
        string text = _hover is not null
            ? _hover.FullName + (_hover.IsAffected ? "  ·  in alert" : "")
            : _scene!.Summary;
        if (!string.IsNullOrEmpty(text))
        {
            var ft = Text(text, 11.5, LabelText, dpi, FaceRegular);
            var r = new Rect(8, bounds.Height - ft.Height - 12, ft.Width + 14, ft.Height + 6);
            dc.DrawRoundedRectangle(ChipBg, null, r, 5, 5);
            dc.DrawText(ft, new Point(r.X + 7, r.Y + 3));
        }

        string hint = Interactive ? "Scroll to zoom · drag to pan · double-click to reset" : IsMouseOver && ExpandCommand is not null ? "Click to enlarge" : "";
        if (hint.Length > 0)
        {
            var ft = Text(hint, 11, HintText, dpi, FaceRegular);
            var r = new Rect(bounds.Width - ft.Width - 22, 8, ft.Width + 14, ft.Height + 6);
            dc.DrawRoundedRectangle(ChipBg, null, r, 5, 5);
            dc.DrawText(ft, new Point(r.X + 7, r.Y + 3));
        }
    }

    private static FormattedText Text(string s, double size, Brush brush, double dpi, Typeface face) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, dpi);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            var d = p - _dragStart;
            _user = _dragUser;
            _user.Translate(d.X, d.Y);
            InvalidateVisual();
            return;
        }
        var hit = HitTest(p);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            InvalidateVisual();
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Interactive) return;
        if (e.ClickCount == 2)
        {
            _user = Matrix.Identity;
            InvalidateVisual();
            return;
        }
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragUser = _user;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            return;
        }
        if (!Interactive && ExpandCommand?.CanExecute(null) == true) ExpandCommand.Execute(null);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Interactive) return;
        double factor = Math.Pow(1.2, e.Delta / 120.0);
        double current = Math.Sqrt(Math.Abs(_user.Determinant));
        factor = Math.Clamp(current * factor, 0.35, 40) / current;
        var p = e.GetPosition(this);
        _user.ScaleAt(factor, factor, p.X, p.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    private MapRegion? HitTest(Point screen)
    {
        if (_scene is null) return null;
        var inverse = ViewMatrix();
        if (!inverse.HasInverse) return null;
        inverse.Invert();
        var w = inverse.Transform(screen);
        foreach (var a in _scene.Affected)
            if (a.Bounds.Contains(w) && a.Geometry.FillContains(w)) return a;
        foreach (var c in _scene.Context)
            if (c.Bounds.Contains(w) && c.Geometry.FillContains(w)) return c;
        foreach (var s in _scene.States)
            if (s.Bounds.Contains(w) && s.Geometry.FillContains(w)) return s;
        return null;
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }
}

internal sealed class MapRegion
{
    public required Geometry Geometry { get; init; }
    public Geometry? Part { get; init; }
    public required Rect Bounds { get; init; }
    public required Point LabelPoint { get; init; }
    public required string ShortName { get; init; }
    public required string FullName { get; init; }
    public bool IsAffected { get; init; }
}

internal sealed class MapScene
{
    private static readonly GeoBounds Conus = new(-124.8, 24.4, -66.9, 49.4);

    public required Rect Focus { get; init; }
    public required List<MapRegion> Affected { get; init; }
    public required List<MapRegion> Context { get; init; }
    public required List<MapRegion> States { get; init; }
    public required string Summary { get; init; }

    public static MapScene? Build(IReadOnlyList<SameLocation> areas, bool wide)
    {
        var atlas = UsAtlas.Instance;
        if (atlas is null) return null;

        var targets = new List<(GeoShape Shape, int Part, bool IsState)>();
        bool nation = false;
        int unmapped = 0;
        foreach (var loc in areas)
        {
            if (loc.StateFips == 0) { nation = true; continue; }
            if (loc.CountyFips == 0 && atlas.State(loc.StateFips) is { } st) { targets.Add((st, 0, true)); continue; }
            if (atlas.County(loc.StateFips, loc.CountyFips) is { } c) { targets.Add((c, loc.Subdivision, false)); continue; }
            unmapped++;
        }
        if (nation)
        {
            foreach (var st in atlas.States.Values.Where(s => s.Bounds.Intersects(Conus))) targets.Add((st, 0, true));
        }
        if (targets.Count == 0) return null;

        var focus = nation ? Conus : targets.Aggregate(GeoBounds.Empty, (b, t) => b.Union(t.Shape.Bounds));
        double minSpan = 0.8;
        if (focus.Width < minSpan) focus = focus.Expand((minSpan - focus.Width) / 2, 0);
        if (focus.Height < minSpan * 0.7) focus = focus.Expand(0, (minSpan * 0.7 - focus.Height) / 2);

        var context = wide
            ? focus.Expand(Math.Max(4, focus.Width * 2.5), Math.Max(3, focus.Height * 2.5))
            : focus.Expand(Math.Max(1.6, focus.Width * 1.6 + focus.Height), Math.Max(0.9, focus.Height * 0.9));
        var projection = new AlbersProjection(focus);

        var targetFips = targets.Where(t => !t.IsState).Select(t => t.Shape.Fips).ToHashSet();
        bool showCounties = focus.Width < 22;

        var affected = new List<MapRegion>();
        foreach (var (shape, part, isState) in targets)
        {
            var geometry = projection.Geometry(shape);
            Geometry? partGeometry = null;
            if (part is >= 1 and <= 9) partGeometry = SubdivisionPart(geometry, part);
            string shortName = isState ? Abbr(shape.Fips) : ShortCounty(shape);
            affected.Add(new MapRegion
            {
                Geometry = geometry,
                Part = partGeometry,
                Bounds = geometry.Bounds,
                LabelPoint = projection.LabelPoint(shape),
                ShortName = shortName,
                FullName = FullName(shape, isState),
                IsAffected = true,
            });
        }

        var contextRegions = new List<MapRegion>();
        if (showCounties)
        {
            foreach (var c in atlas.Counties.Values)
            {
                if (targetFips.Contains(c.Fips) || !c.Bounds.Intersects(context)) continue;
                var g = projection.Geometry(c);
                contextRegions.Add(new MapRegion
                {
                    Geometry = g,
                    Bounds = g.Bounds,
                    LabelPoint = default,
                    ShortName = c.Name,
                    FullName = FullName(c, false),
                });
            }
        }

        var stateRegions = new List<MapRegion>();
        foreach (var s in atlas.States.Values)
        {
            if (!s.Bounds.Intersects(context)) continue;
            var g = projection.Geometry(s);
            stateRegions.Add(new MapRegion
            {
                Geometry = g,
                Bounds = g.Bounds,
                LabelPoint = projection.LabelPoint(s),
                ShortName = Abbr(s.Fips),
                FullName = s.Name,
            });
        }

        int counties = targets.Count(t => !t.IsState);
        int states = targets.Count(t => t.IsState);
        var parts = new List<string>();
        if (nation) parts.Add("Entire United States (AK, HI and territories not shown)");
        else
        {
            if (counties > 0) parts.Add(counties == 1 ? "1 county" : $"{counties} counties");
            if (states > 0) parts.Add(states == 1 ? "1 entire state" : $"{states} entire states");
        }
        if (unmapped > 0) parts.Add($"{unmapped} marine/other area{(unmapped == 1 ? "" : "s")} not shown");

        var focusRect = Rect.Empty;
        foreach (var a in affected) focusRect.Union(a.Bounds);
        if (nation) focusRect = projection.Rect(Conus);
        var minRect = projection.Rect(focus);
        focusRect.Union(minRect);

        return new MapScene
        {
            Focus = focusRect,
            Affected = affected,
            Context = contextRegions,
            States = stateRegions,
            Summary = string.Join("  ·  ", parts),
        };
    }

    private static Geometry SubdivisionPart(Geometry county, int part)
    {
        var b = county.Bounds;
        int col = (part - 1) % 3, row = (part - 1) / 3;
        var cell = new Rect(b.Left + col * b.Width / 3, b.Top + row * b.Height / 3, b.Width / 3, b.Height / 3);
        cell.Inflate(b.Width * 0.04, b.Height * 0.04);
        var clipped = Geometry.Combine(county, new RectangleGeometry(cell), GeometryCombineMode.Intersect, null);
        clipped.Freeze();
        return clipped;
    }

    private static string Abbr(int stateFips) => SameCodes.StateAbbreviation(stateFips) ?? stateFips.ToString("00");

    private static string ShortCounty(GeoShape c) => c.Name;

    private static string FullName(GeoShape shape, bool isState)
    {
        if (isState) return shape.Name;
        int state = shape.Fips / 1000, county = shape.Fips % 1000;
        return $"{SameCodes.CountyName(state, county) ?? shape.Name}, {Abbr(state)}";
    }
}

internal sealed class AlbersProjection
{
    private const double Rad = Math.PI / 180;
    private const double Scale = 1000;
    private readonly double _n, _c, _rho0, _lon0, _lat0, _cosLat0;
    private readonly bool _equirect;

    public AlbersProjection(GeoBounds focus)
    {
        _lon0 = focus.CenterLon;
        _lat0 = focus.CenterLat;
        double span = Math.Max(focus.Height, 2);
        double phi1 = (_lat0 - span / 3) * Rad, phi2 = (_lat0 + span / 3) * Rad;
        _n = (Math.Sin(phi1) + Math.Sin(phi2)) / 2;
        _cosLat0 = Math.Cos(_lat0 * Rad);
        _equirect = Math.Abs(_n) < 0.1;
        if (!_equirect)
        {
            _c = Math.Cos(phi1) * Math.Cos(phi1) + 2 * _n * Math.Sin(phi1);
            _rho0 = Math.Sqrt(Math.Max(0, _c - 2 * _n * Math.Sin(_lat0 * Rad))) / _n;
        }
    }

    public Point Project(double lon, double lat)
    {
        if (_equirect) return new Point((lon - _lon0) * Rad * _cosLat0 * Scale, -(lat - _lat0) * Rad * Scale);
        double rho = Math.Sqrt(Math.Max(0, _c - 2 * _n * Math.Sin(lat * Rad))) / _n;
        double theta = _n * (lon - _lon0) * Rad;
        return new Point(rho * Math.Sin(theta) * Scale, -(_rho0 - rho * Math.Cos(theta)) * Scale);
    }

    public Rect Rect(GeoBounds b)
    {
        var r = new Rect(Project(b.MinLon, b.MinLat), Project(b.MaxLon, b.MaxLat));
        r.Union(Project(b.MinLon, b.MaxLat));
        r.Union(Project(b.MaxLon, b.MinLat));
        r.Union(Project(b.CenterLon, b.MaxLat));
        return r;
    }

    public Geometry Geometry(GeoShape shape)
    {
        var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var ctx = g.Open())
        {
            foreach (var ring in shape.Rings)
            {
                if (ring.Length < 6) continue;
                ctx.BeginFigure(Project(ring[0], ring[1]), true, true);
                var pts = new List<Point>(ring.Length / 2);
                for (int i = 2; i < ring.Length; i += 2) pts.Add(Project(ring[i], ring[i + 1]));
                ctx.PolyLineTo(pts, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    public Point LabelPoint(GeoShape shape)
    {
        double bestArea = -1;
        Point best = default;
        foreach (var ring in shape.Rings)
        {
            int n = ring.Length / 2;
            if (n < 3) continue;
            double a = 0, cx = 0, cy = 0;
            var prev = Project(ring[(n - 1) * 2], ring[(n - 1) * 2 + 1]);
            for (int i = 0; i < n; i++)
            {
                var p = Project(ring[i * 2], ring[i * 2 + 1]);
                double cross = prev.X * p.Y - p.X * prev.Y;
                a += cross;
                cx += (prev.X + p.X) * cross;
                cy += (prev.Y + p.Y) * cross;
                prev = p;
            }
            if (Math.Abs(a) < 1e-12) continue;
            if (Math.Abs(a) > bestArea)
            {
                bestArea = Math.Abs(a);
                best = new Point(cx / (3 * a), cy / (3 * a));
            }
        }
        return best;
    }
}
