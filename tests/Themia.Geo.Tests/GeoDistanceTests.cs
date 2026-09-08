using Themia.Geo;
using Xunit;

namespace Themia.Geo.Tests;

public sealed class GeoDistanceTests
{
    // The reference distance is Vincenty's inverse formula on the WGS84 ellipsoid, implemented as a
    // private helper below — a different algorithm (iterative, ellipsoidal) on a different earth model
    // than GeoDistance.HaversineMetres (closed-form, spherical), so this reference does not depend on
    // anything defined in the package under test. An expected value produced by the formula under test
    // would prove only that it agrees with its own arithmetic.
    //
    // Tolerance is 0.6%: the measured sphere-vs-ellipsoid gap between the two methods at these
    // latitudes/distances is ~0.4-0.5%, so 0.6% leaves headroom without being loose enough to hide a
    // real defect in HaversineMetres.
    [Theory]
    [InlineData(13.7563, 100.5018, 18.7883, 98.9853)]   // Bangkok -> Chiang Mai
    [InlineData(13.7563, 100.5018, 1.3521, 103.8198)]   // Bangkok -> Singapore
    public void Haversine_matches_an_independent_ellipsoidal_reference(double aLat, double aLng, double bLat, double bLng)
    {
        var expected = VincentyMetres(aLat, aLng, bLat, bLng);
        var actual = GeoDistance.HaversineMetres(new GeoPoint(aLat, aLng), new GeoPoint(bLat, bLng));
        Assert.InRange(actual, expected * 0.994, expected * 1.006);
    }

    [Fact]
    public void Equirectangular_tracks_haversine_over_short_thai_distances()
    {
        // The reason propertiezy chose it: at ~2 km and Thai latitudes the two agree to centimetres.
        var a = new GeoPoint(13.7563, 100.5018);
        var b = new GeoPoint(13.7743, 100.5018);   // ~2 km due north
        Assert.InRange(
            Math.Abs(GeoDistance.HaversineMetres(a, b) - GeoDistance.EquirectangularMetres(a, b)),
            0, 0.10);
    }

    [Fact]
    public void Distance_to_self_is_zero()
        => Assert.Equal(0, GeoDistance.HaversineMetres(new GeoPoint(13.75, 100.5), new GeoPoint(13.75, 100.5)), 6);

    // Vincenty's inverse formula for geodesic distance on the WGS84 ellipsoid (T. Vincenty, 1975).
    // Deliberately independent of GeoDistance: different algorithm, different earth model, so it does
    // not share a bug with — or simply restate — the haversine implementation under test.
    private static double VincentyMetres(double lat1Degrees, double lng1Degrees, double lat2Degrees, double lng2Degrees)
    {
        const double semiMajorAxis = 6_378_137.0;         // WGS84 'a', metres
        const double flattening = 1.0 / 298.257223563;    // WGS84 'f'
        const double semiMinorAxis = (1 - flattening) * semiMajorAxis;

        var l = ToRadians(lng2Degrees - lng1Degrees);
        var reducedLat1 = Math.Atan((1 - flattening) * Math.Tan(ToRadians(lat1Degrees)));
        var reducedLat2 = Math.Atan((1 - flattening) * Math.Tan(ToRadians(lat2Degrees)));
        var sinU1 = Math.Sin(reducedLat1);
        var cosU1 = Math.Cos(reducedLat1);
        var sinU2 = Math.Sin(reducedLat2);
        var cosU2 = Math.Cos(reducedLat2);

        var lambda = l;
        double cosSqAlpha = 0, sinSigma = 0, cosSigma = 0, sigma = 0, cos2SigmaM = 0;

        for (var iteration = 0; iteration < 200; iteration++)
        {
            var sinLambda = Math.Sin(lambda);
            var cosLambda = Math.Cos(lambda);
            sinSigma = Math.Sqrt(
                Math.Pow(cosU2 * sinLambda, 2) +
                Math.Pow(cosU1 * sinU2 - sinU1 * cosU2 * cosLambda, 2));

            if (sinSigma == 0)
                return 0; // Coincident points.

            cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
            sigma = Math.Atan2(sinSigma, cosSigma);

            var sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSqAlpha = 1 - sinAlpha * sinAlpha;
            cos2SigmaM = cosSqAlpha != 0 ? cosSigma - 2 * sinU1 * sinU2 / cosSqAlpha : 0;

            var c = flattening / 16 * cosSqAlpha * (4 + flattening * (4 - 3 * cosSqAlpha));
            var previousLambda = lambda;
            lambda = l + (1 - c) * flattening * sinAlpha *
                (sigma + c * sinSigma * (cos2SigmaM + c * cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM)));

            if (Math.Abs(lambda - previousLambda) < 1e-12)
                break;
        }

        var uSquared = cosSqAlpha * (semiMajorAxis * semiMajorAxis - semiMinorAxis * semiMinorAxis) / (semiMinorAxis * semiMinorAxis);
        var bigA = 1 + uSquared / 16384 * (4096 + uSquared * (-768 + uSquared * (320 - 175 * uSquared)));
        var bigB = uSquared / 1024 * (256 + uSquared * (-128 + uSquared * (74 - 47 * uSquared)));
        var deltaSigma = bigB * sinSigma * (cos2SigmaM + bigB / 4 *
            (cosSigma * (-1 + 2 * cos2SigmaM * cos2SigmaM) -
             bigB / 6 * cos2SigmaM * (-3 + 4 * sinSigma * sinSigma) * (-3 + 4 * cos2SigmaM * cos2SigmaM)));

        return semiMinorAxis * bigA * (sigma - deltaSigma);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
