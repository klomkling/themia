namespace Themia.Geo;

/// <summary>A latitude/longitude bounding box, sized to fully contain a radius around a centre point.</summary>
/// <remarks>
/// Meant as a cheap SQL prefilter ahead of an exact distance check: an indexed range scan on
/// latitude/longitude columns narrows candidate rows before <see cref="GeoDistance"/> is applied to
/// the survivors. The box widens longitude with <c>1/cos(latitude)</c> so it stays wide enough
/// east-west at any latitude — a box built with a fixed degrees-per-metre is too narrow off the
/// equator and would silently drop rows that are actually inside the radius.
/// </remarks>
public readonly record struct GeoBounds
{
    private const double MetresPerDegreeLatitude = 111_320.0;

    // cos(latitude) approaches zero at the poles and the division blows up. Clamping yields a wide box
    // instead of NaN. No Thai input reaches this, which is exactly why an unguarded division would go
    // unnoticed until something fed it a bad point.
    private const double MinimumCosine = 0.01;

    private GeoBounds(GeoPoint southWest, GeoPoint northEast)
    {
        SouthWest = southWest;
        NorthEast = northEast;
    }

    /// <summary>The box's south-west corner.</summary>
    public GeoPoint SouthWest { get; }

    /// <summary>The box's north-east corner.</summary>
    public GeoPoint NorthEast { get; }

    /// <summary>Builds a box that fully contains a circle of the given radius around <paramref name="centre"/>.</summary>
    /// <param name="centre">The circle's centre.</param>
    /// <param name="metres">The circle's radius, in metres. Must not be negative.</param>
    /// <returns>The bounding box.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="metres"/> is negative.</exception>
    public static GeoBounds AroundMetres(GeoPoint centre, double metres)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(metres);

        var latDelta = metres / MetresPerDegreeLatitude;
        var cos = Math.Max(Math.Cos(centre.Latitude * Math.PI / 180.0), MinimumCosine);
        var lngDelta = metres / (MetresPerDegreeLatitude * cos);

        return new GeoBounds(
            new GeoPoint(Math.Max(centre.Latitude - latDelta, -90), Math.Max(centre.Longitude - lngDelta, -180)),
            new GeoPoint(Math.Min(centre.Latitude + latDelta, 90), Math.Min(centre.Longitude + lngDelta, 180)));
    }

    /// <summary>Whether <paramref name="point"/> falls within this box, inclusive of its edges.</summary>
    /// <param name="point">The point to test.</param>
    /// <returns><see langword="true"/> when the point is inside or on the boundary of the box.</returns>
    public bool Contains(GeoPoint point) =>
        point.Latitude >= SouthWest.Latitude && point.Latitude <= NorthEast.Latitude &&
        point.Longitude >= SouthWest.Longitude && point.Longitude <= NorthEast.Longitude;
}
