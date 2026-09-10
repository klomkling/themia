namespace Themia.Geo;

/// <summary>Great-circle distance formulas between two <see cref="GeoPoint"/> values.</summary>
/// <remarks>
/// Works in <see langword="double"/>, not <see langword="decimal"/>: every trig function in .NET
/// takes <see langword="double"/>, and converting on entry and exit of each call is worse than
/// converting once at the persistence boundary where coordinates are stored as <c>decimal(9,6)</c>.
/// </remarks>
public static class GeoDistance
{
    // IUGG mean Earth radius.
    private const double EarthRadiusMetres = 6_371_008.8;

    /// <summary>
    /// Computes the great-circle distance between two points using the haversine formula. Accurate at
    /// any distance.
    /// </summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    /// <returns>The distance, in metres.</returns>
    public static double HaversineMetres(GeoPoint a, GeoPoint b)
    {
        var dLat = ToRadians(b.Latitude - a.Latitude);
        var dLng = ToRadians(b.Longitude - a.Longitude);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(a.Latitude)) * Math.Cos(ToRadians(b.Latitude))
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>
    /// Approximates the distance between two points using the equirectangular projection. Cheaper than
    /// <see cref="HaversineMetres"/> and accurate only over short distances (a few kilometres).
    /// </summary>
    /// <param name="a">The first point.</param>
    /// <param name="b">The second point.</param>
    /// <returns>The approximate distance, in metres.</returns>
    public static double EquirectangularMetres(GeoPoint a, GeoPoint b)
    {
        var meanLat = ToRadians((a.Latitude + b.Latitude) / 2);
        var x = ToRadians(b.Longitude - a.Longitude) * Math.Cos(meanLat);
        var y = ToRadians(b.Latitude - a.Latitude);
        return EarthRadiusMetres * Math.Sqrt(x * x + y * y);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
