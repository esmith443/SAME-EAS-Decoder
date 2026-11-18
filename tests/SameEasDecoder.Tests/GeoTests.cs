using SameEasDecoder.Core.Geo;
using SameEasDecoder.Core.Same;

namespace SameEasDecoder.Tests;

public class GeoTests
{
    [Fact]
    public void Atlas_loads_counties_and_states()
    {
        var atlas = UsAtlas.Instance;
        Assert.NotNull(atlas);
        Assert.InRange(atlas.Counties.Count, 3200, 3300);
        Assert.InRange(atlas.States.Count, 50, 60);

        var washtenaw = atlas.County(26, 161);
        Assert.NotNull(washtenaw);
        Assert.Equal("Washtenaw", washtenaw.Name);
        Assert.InRange(washtenaw.Bounds.CenterLon, -84.2, -83.6);
        Assert.InRange(washtenaw.Bounds.CenterLat, 42.1, 42.4);
        Assert.Equal("Michigan", atlas.State(26)?.Name);
    }

    [Fact]
    public void Alaska_is_contiguous_across_the_antimeridian()
    {
        var ak = UsAtlas.Instance!.State(2)!;
        Assert.True(ak.Bounds.MaxLon < 0);
        Assert.True(ak.Bounds.MinLon < -180);
    }

    [Theory]
    [InlineData(26, 161, "Washtenaw County")]
    [InlineData(22, 71, "Orleans Parish")]
    [InlineData(51, 760, "Richmond City")]
    [InlineData(51, 159, "Richmond County")]
    [InlineData(32, 510, "Carson City")]
    [InlineData(72, 127, "San Juan")]
    public void County_names_get_the_right_suffix(int state, int county, string expected)
    {
        Assert.Equal(expected, SameCodes.CountyName(state, county));
    }

    [Fact]
    public void Location_descriptions_use_county_names()
    {
        Assert.True(SameHeader.TryParse("ZCZC-WXR-TOR-026161-326075+0045-3211704-KDTX/NWS-", DateTime.UtcNow, out var h));
        Assert.Equal("Washtenaw County, MI", h!.Locations[0].Description);
        Assert.Equal("Northeast Jackson County, MI", h.Locations[1].Description);
    }
}
