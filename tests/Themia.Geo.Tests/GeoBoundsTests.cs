using Themia.Geo;
using Xunit;

namespace Themia.Geo.Tests;

public sealed class GeoBoundsTests
{
    // THE test for this type. A box built with a fixed degrees-per-metre is too narrow east-west at any
    // latitude off the equator, so the SQL prefilter silently drops rows inside the radius — and a
    // north-south-only test passes with that bug present.
    [Fact]
    public void A_point_due_east_at_exactly_the_radius_is_inside_the_box()
    {
        var centre = new GeoPoint(13.7563, 100.5018);       // Bangkok
        var bounds = GeoBounds.AroundMetres(centre, 2_000);

        var eastDegrees = 2_000.0 / (111_320.0 * Math.Cos(centre.Latitude * Math.PI / 180.0));
        var due_east = new GeoPoint(centre.Latitude, centre.Longitude + eastDegrees);

        Assert.True(bounds.Contains(due_east));
    }

    [Fact]
    public void A_point_well_outside_the_radius_is_not_in_the_box()
        => Assert.False(GeoBounds.AroundMetres(new GeoPoint(13.7563, 100.5018), 2_000)
            .Contains(new GeoPoint(18.7883, 98.9853)));

    [Fact]
    public void Near_a_pole_the_box_is_finite()
    {
        var bounds = GeoBounds.AroundMetres(new GeoPoint(89.999, 0), 10_000);
        Assert.True(double.IsFinite(bounds.NorthEast.Longitude));
        Assert.True(double.IsFinite(bounds.SouthWest.Longitude));
    }
}
