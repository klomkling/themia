namespace Themia.Geo;

/// <summary>A validated geographic coordinate.</summary>
/// <remarks>
/// Validation happens once, at construction, so every consumer downstream — distance formulas,
/// bounding boxes, SQL parameters — can trust <see cref="Latitude"/> and <see cref="Longitude"/>
/// without re-checking. <c>NaN</c> is the value that matters most to reject: it survives every trig
/// function and compares false against every threshold, so one degenerate point silently makes a
/// radius filter return nothing while reporting success.
/// </remarks>
public readonly record struct GeoPoint
{
    /// <summary>Creates a point, throwing if the coordinates cannot describe a real place.</summary>
    /// <param name="latitude">Degrees, must be finite and in [-90, 90].</param>
    /// <param name="longitude">Degrees, must be finite and in [-180, 180].</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="latitude"/> or <paramref name="longitude"/> is not finite or out of range.
    /// </exception>
    public GeoPoint(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be a finite value in [-90, 90].");
        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be a finite value in [-180, 180].");

        Latitude = latitude;
        Longitude = longitude;
    }

    /// <summary>Degrees north of the equator, in [-90, 90].</summary>
    public double Latitude { get; }

    /// <summary>Degrees east of the prime meridian, in [-180, 180].</summary>
    public double Longitude { get; }

    /// <summary>Attempts to create a point, returning <see langword="false"/> instead of throwing.</summary>
    /// <param name="latitude">Degrees, must be finite and in [-90, 90].</param>
    /// <param name="longitude">Degrees, must be finite and in [-180, 180].</param>
    /// <param name="point">The created point, or <c>default</c> when the coordinates are invalid.</param>
    /// <returns><see langword="true"/> when the coordinates describe a real place.</returns>
    public static bool TryCreate(double latitude, double longitude, out GeoPoint point)
    {
        if (double.IsFinite(latitude) && latitude is >= -90 and <= 90 &&
            double.IsFinite(longitude) && longitude is >= -180 and <= 180)
        {
            point = new GeoPoint(latitude, longitude);
            return true;
        }

        point = default;
        return false;
    }
}
