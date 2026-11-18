using System.Text.Json;

namespace SameEasDecoder.Core.Geo;

public readonly record struct GeoBounds(double MinLon, double MinLat, double MaxLon, double MaxLat)
{
    public static GeoBounds Empty => new(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

    public bool IsEmpty => MinLon > MaxLon || MinLat > MaxLat;
    public double Width => MaxLon - MinLon;
    public double Height => MaxLat - MinLat;
    public double CenterLon => (MinLon + MaxLon) / 2;
    public double CenterLat => (MinLat + MaxLat) / 2;

    public GeoBounds Union(GeoBounds o) => IsEmpty ? o : o.IsEmpty ? this :
        new(Math.Min(MinLon, o.MinLon), Math.Min(MinLat, o.MinLat), Math.Max(MaxLon, o.MaxLon), Math.Max(MaxLat, o.MaxLat));

    public GeoBounds Expand(double dLon, double dLat) => new(MinLon - dLon, MinLat - dLat, MaxLon + dLon, MaxLat + dLat);

    public bool Intersects(GeoBounds o) => !IsEmpty && !o.IsEmpty &&
        MinLon <= o.MaxLon && MaxLon >= o.MinLon && MinLat <= o.MaxLat && MaxLat >= o.MinLat;
}

public sealed class GeoShape
{
    public required int Fips { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<double[]> Rings { get; init; }
    public required GeoBounds Bounds { get; init; }
}

public sealed class UsAtlas
{
    private const string ResourceName = "SameEasDecoder.Core.Data.us-counties-10m.json";
    private static readonly Lazy<UsAtlas?> Shared = new(LoadEmbedded, LazyThreadSafetyMode.ExecutionAndPublication);

    private UsAtlas(Dictionary<int, GeoShape> counties, Dictionary<int, GeoShape> states)
    {
        Counties = counties;
        States = states;
    }

    public static UsAtlas? Instance => Shared.Value;

    public IReadOnlyDictionary<int, GeoShape> Counties { get; }
    public IReadOnlyDictionary<int, GeoShape> States { get; }

    public GeoShape? County(int stateFips, int countyFips) => Counties.GetValueOrDefault(stateFips * 1000 + countyFips);
    public GeoShape? State(int stateFips) => States.GetValueOrDefault(stateFips);

    private static UsAtlas? LoadEmbedded()
    {
        try
        {
            using var stream = typeof(UsAtlas).Assembly.GetManifestResourceStream(ResourceName);
            return stream is null ? null : Parse(stream);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    public static UsAtlas Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var transform = root.GetProperty("transform");
        double sx = transform.GetProperty("scale")[0].GetDouble(), sy = transform.GetProperty("scale")[1].GetDouble();
        double tx = transform.GetProperty("translate")[0].GetDouble(), ty = transform.GetProperty("translate")[1].GetDouble();

        var arcsElement = root.GetProperty("arcs");
        var arcs = new double[arcsElement.GetArrayLength()][];
        int ai = 0;
        foreach (var arc in arcsElement.EnumerateArray())
        {
            var pts = new double[arc.GetArrayLength() * 2];
            long x = 0, y = 0;
            int k = 0;
            foreach (var p in arc.EnumerateArray())
            {
                x += p[0].GetInt64();
                y += p[1].GetInt64();
                pts[k++] = x * sx + tx;
                pts[k++] = y * sy + ty;
            }
            arcs[ai++] = pts;
        }

        var objects = root.GetProperty("objects");
        return new UsAtlas(ReadShapes(objects.GetProperty("counties"), arcs), ReadShapes(objects.GetProperty("states"), arcs));
    }

    private static Dictionary<int, GeoShape> ReadShapes(JsonElement collection, double[][] arcs)
    {
        var result = new Dictionary<int, GeoShape>();
        foreach (var g in collection.GetProperty("geometries").EnumerateArray())
        {
            if (!g.TryGetProperty("id", out var idEl) || !int.TryParse(idEl.GetString(), out int fips)) continue;
            if (!g.TryGetProperty("arcs", out var arcsEl) || !g.TryGetProperty("type", out var typeEl)) continue;

            string name = g.TryGetProperty("properties", out var props) && props.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            bool alaska = fips == 2 || fips / 1000 == 2;
            var rings = new List<double[]>();

            if (typeEl.GetString() == "Polygon")
            {
                foreach (var ring in arcsEl.EnumerateArray()) rings.Add(BuildRing(ring, arcs, alaska));
            }
            else if (typeEl.GetString() == "MultiPolygon")
            {
                foreach (var polygon in arcsEl.EnumerateArray())
                    foreach (var ring in polygon.EnumerateArray()) rings.Add(BuildRing(ring, arcs, alaska));
            }
            if (rings.Count == 0) continue;

            var bounds = GeoBounds.Empty;
            foreach (var r in rings)
                for (int i = 0; i < r.Length; i += 2)
                    bounds = bounds.Union(new GeoBounds(r[i], r[i + 1], r[i], r[i + 1]));

            result[fips] = new GeoShape { Fips = fips, Name = name, Rings = rings, Bounds = bounds };
        }
        return result;
    }

    private static double[] BuildRing(JsonElement ring, double[][] arcs, bool alaska)
    {
        var points = new List<double>();
        foreach (var indexEl in ring.EnumerateArray())
        {
            int index = indexEl.GetInt32();
            var arc = arcs[index >= 0 ? index : ~index];
            int count = arc.Length / 2;
            for (int j = 0; j < count; j++)
            {
                if (points.Count > 0 && j == 0) continue;
                int src = index >= 0 ? j : count - 1 - j;
                double lon = arc[src * 2], lat = arc[src * 2 + 1];
                if (alaska && lon > 0) lon -= 360;
                points.Add(lon);
                points.Add(lat);
            }
        }
        return points.ToArray();
    }
}
