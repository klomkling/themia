using Themia.Geo;
using Xunit;

namespace Themia.Geo.Tests;

public sealed class GeoPointTests
{
    [Theory]
    [InlineData(91, 0)]
    [InlineData(-91, 0)]
    [InlineData(0, 181)]
    [InlineData(0, -181)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    public void Rejects_values_that_cannot_describe_a_place(double lat, double lng)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(lat, lng));
        Assert.False(GeoPoint.TryCreate(lat, lng, out _));
    }

    // NaN is the one that matters: it survives every formula and compares false against every
    // threshold, so one degenerate point makes a radius filter return nothing and report success.
    [Fact]
    public void Accepts_the_extremes_that_are_real_places()
    {
        Assert.Equal(90, new GeoPoint(90, 180).Latitude);
        Assert.Equal(-180, new GeoPoint(-90, -180).Longitude);
    }
}
