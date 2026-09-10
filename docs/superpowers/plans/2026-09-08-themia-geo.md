# Themia.Geo Implementation Plan (0.24.0)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Ship coordinate primitives and a geocoding seam that both consumer apps asked for by name, and
nothing else.

**Architecture:** Two framework-neutral packages. `Themia.Geo` holds value types and pure math;
`Themia.Geo.Google` holds one HTTP client. No database, no tenant state, no module.

**Tech Stack:** .NET 8 + .NET 10, `System.Text.Json`, `IHttpClientFactory`, xUnit. **No Testcontainers** —
nothing here touches a database.

**Spec:** `docs/superpowers/specs/2026-09-08-themia-geo-design.md`. Read it before Task 1; it carries every
"why" and four rounds of review are recorded in its exclusions.

**Ships with:** `docs/superpowers/plans/2026-09-08-themia-ai.md`. That plan's final task performs the
shared `0.24.0` release for **both** packages. This plan does not bump the version.

## Global Constraints

- **TFMs:** both packages `net8.0;net10.0`. Non-negotiable (`CLAUDE.md` target-framework policy).
- **`Themia.Geo` must reference no `Themia.Framework.*` package and no database.** Task 3 adds the test.
- `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true`. A clean `--no-incremental` build must
  report zero warnings and no `RS0016`.
- **Every new public member goes into that project's `PublicAPI.Unshipped.txt` in the task that adds it.**
  `RS0016` is a build error, so deferring this to the last task means the intermediate tasks cannot build.
- **Every enum reserves `0` for `Unspecified`** and is validated with `Enum.IsDefined`.
- `System.Text.Json` only — never `Newtonsoft.Json`. `ILogger<T>` only — no `Console.*`.
- Central package management: versions live in `Directory.Packages.props`, never in a csproj.
- Version stays at whatever `Directory.Build.props` currently holds. The AI plan bumps it.
- Commit after every task. Conventional commits, imperative mood, **no co-author or "generated with"
  trailers**.
- **Do not `git push` and do not open a PR Mon-Fri 09:00-18:00** — `klomkling/themia` is a public repo and
  the owner does not land public commits during working hours. Committing locally is fine.

---

## File Structure

```
src/neutral/Themia.Geo/
  GeoPoint.cs
  GeoDistance.cs
  GeoBounds.cs
  IGeocodingProvider.cs        # + GeocodeResult, GeocodeOutcome, GeocodeOptions
  GeoOptions.cs
  DependencyInjection/GeoServiceCollectionExtensions.cs
src/neutral/Themia.Geo.Google/
  GoogleGeocodingProvider.cs
  GoogleGeocodingOptions.cs
  DependencyInjection/GoogleGeoServiceCollectionExtensions.cs
tests/Themia.Geo.Tests/
  GeoPointTests.cs  GeoDistanceTests.cs  GeoBoundsTests.cs  LayeringTests.cs
tests/Themia.Geo.Google.Tests/
  GoogleGeocodingProviderTests.cs
  Fixtures/google-ok.json  google-zero-results.json  google-over-query-limit.json  google-error.json
```

---

## Task 1: Value types and math

**Files:**
- Create: `src/neutral/Themia.Geo/Themia.Geo.csproj`, `GeoPoint.cs`, `GeoDistance.cs`, `GeoBounds.cs`
- Test: `tests/Themia.Geo.Tests/{GeoPointTests,GeoDistanceTests,GeoBoundsTests}.cs`

**Interfaces:**
- Produces: `GeoPoint` (ctor + `TryCreate`), `GeoDistance.HaversineMetres`,
  `GeoDistance.EquirectangularMetres`, `GeoBounds.AroundMetres`, `GeoBounds.Contains`.

Follow `src/neutral/Themia.Totp/` for csproj shape and XML-doc style — it is the closest small neutral
package. Wire `PublicAPI.Shipped.txt` (empty) and `PublicAPI.Unshipped.txt` as `<AdditionalFiles>` exactly
as that project does.

- [ ] **Step 1: Write the failing `GeoPoint` tests**

```csharp
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
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/Themia.Geo.Tests`. Expected: does not
      compile, type not found.

- [ ] **Step 3: Implement `GeoPoint`**

```csharp
public readonly record struct GeoPoint
{
    public GeoPoint(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be a finite value in [-90, 90].");
        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be a finite value in [-180, 180].");

        Latitude = latitude;
        Longitude = longitude;
    }

    public double Latitude { get; }
    public double Longitude { get; }

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
```

- [ ] **Step 4: Run — expect PASS**

- [ ] **Step 5: Write the failing distance tests**

```csharp
// Reference pairs with published great-circle distances. NOT computed by either formula in this
// file: an expected value derived from the implementation proves only that it agrees with itself.
// Bangkok (13.7563, 100.5018) -> Chiang Mai (18.7883, 98.9853) = 586.1 km  (±1 km, sphere vs ellipsoid)
// Bangkok -> Singapore (1.3521, 103.8198) = 1425 km
[Theory]
[InlineData(13.7563, 100.5018, 18.7883, 98.9853, 586_100)]
[InlineData(13.7563, 100.5018, 1.3521, 103.8198, 1_425_000)]
public void Haversine_matches_published_distances(double aLat, double aLng, double bLat, double bLng, double expected)
{
    var actual = GeoDistance.HaversineMetres(new GeoPoint(aLat, aLng), new GeoPoint(bLat, bLng));
    Assert.InRange(actual, expected * 0.995, expected * 1.005);
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
```

- [ ] **Step 6: Run to verify failure**

- [ ] **Step 7: Implement `GeoDistance`**

```csharp
public static class GeoDistance
{
    private const double EarthRadiusMetres = 6_371_008.8;   // IUGG mean radius

    public static double HaversineMetres(GeoPoint a, GeoPoint b)
    {
        var dLat = ToRadians(b.Latitude - a.Latitude);
        var dLng = ToRadians(b.Longitude - a.Longitude);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRadians(a.Latitude)) * Math.Cos(ToRadians(b.Latitude))
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * EarthRadiusMetres * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    public static double EquirectangularMetres(GeoPoint a, GeoPoint b)
    {
        var meanLat = ToRadians((a.Latitude + b.Latitude) / 2);
        var x = ToRadians(b.Longitude - a.Longitude) * Math.Cos(meanLat);
        var y = ToRadians(b.Latitude - a.Latitude);
        return EarthRadiusMetres * Math.Sqrt(x * x + y * y);
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
```

- [ ] **Step 8: Run — expect PASS**

- [ ] **Step 9: Write the failing bounds tests**

```csharp
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
```

- [ ] **Step 10: Run to verify failure**

- [ ] **Step 11: Implement `GeoBounds`**

```csharp
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

    public GeoPoint SouthWest { get; }
    public GeoPoint NorthEast { get; }

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

    public bool Contains(GeoPoint point) =>
        point.Latitude >= SouthWest.Latitude && point.Latitude <= NorthEast.Latitude &&
        point.Longitude >= SouthWest.Longitude && point.Longitude <= NorthEast.Longitude;
}
```

- [ ] **Step 12: Run — expect PASS**

- [ ] **Step 13: Fill `PublicAPI.Unshipped.txt`, add both projects to `Themia.sln`, build clean**

Run `dotnet build Themia.sln --no-incremental` and confirm zero warnings and no `RS0016`.

- [ ] **Step 14: Commit**

```bash
git add src/neutral/Themia.Geo tests/Themia.Geo.Tests Themia.sln
git commit -m "feat(geo): GeoPoint, distance formulas and bounding boxes"
```

---

## Task 2: Geocoding contract and the Google provider

**Files:**
- Create: `src/neutral/Themia.Geo/IGeocodingProvider.cs`,
  `src/neutral/Themia.Geo.Google/{Themia.Geo.Google.csproj,GoogleGeocodingProvider.cs,GoogleGeocodingOptions.cs}`
- Test: `tests/Themia.Geo.Google.Tests/GoogleGeocodingProviderTests.cs` + `Fixtures/*.json`

**Interfaces:**
- Consumes: `GeoPoint` (Task 1).
- Produces: `IGeocodingProvider.GeocodeAsync`, `GeocodeResult`, `GeocodeOutcome`, `GeocodeOptions`,
  `AddThemiaGeoGoogle(...)`.

**Before writing the provider, capture the fixtures.** Each `Fixtures/*.json` must be a response body
captured from a real Google Geocoding call, saved with a header comment recording the date and the query
that produced it. A hand-written payload makes the expected value and the implementation share an author:
if Google's real quota status string is not what was assumed, the test passes and production takes the
wrong branch. If a status cannot be captured (deliberately exhausting a quota is not reasonable), copy it
from Google's published status documentation and mark that fixture `// UNPROVEN: from docs, not captured`.

- [ ] **Step 1: Write the failing outcome-mapping tests**

```csharp
[Theory]
[InlineData("google-ok.json", GeocodeOutcome.Found)]
[InlineData("google-zero-results.json", GeocodeOutcome.NotFound)]
[InlineData("google-over-query-limit.json", GeocodeOutcome.ProviderLimit)]
[InlineData("google-error.json", GeocodeOutcome.ProviderError)]
public async Task Maps_each_captured_google_status(string fixture, GeocodeOutcome expected)
{
    var provider = ProviderReturning(await File.ReadAllTextAsync(Path.Combine("Fixtures", fixture)));
    var result = await provider.GeocodeAsync("anything", null, CancellationToken.None);
    Assert.Equal(expected, result.Outcome);
}

// ProviderLimit exists so a batch backfill stops instead of spending the rest of the day's allowance
// retrying calls that cannot succeed. It only pays off if it is distinguishable.
[Fact]
public async Task Over_query_limit_is_not_reported_as_a_transient_error()
{
    var provider = ProviderReturning(await File.ReadAllTextAsync("Fixtures/google-over-query-limit.json"));
    Assert.NotEqual(GeocodeOutcome.ProviderError, (await provider.GeocodeAsync("x", null, default)).Outcome);
}

[Fact]
public async Task A_found_result_carries_the_coordinate()
{
    var provider = ProviderReturning(await File.ReadAllTextAsync("Fixtures/google-ok.json"));
    var result = await provider.GeocodeAsync("x", null, default);
    Assert.NotNull(result.Point);
}
```

`ProviderReturning(fixturePath)` builds the provider over a stub primary handler returning the fixture
body — no network call, so the suite stays runnable without egress:

```csharp
internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}
```

Register it with `ConfigurePrimaryHttpMessageHandler` so the request still passes through
`LoggingHttpMessageHandler` — Step 6's key-disclosure test depends on that handler actually running.

- [ ] **Step 2: Run to verify failure**

- [ ] **Step 3: Implement the contract in `Themia.Geo`**

```csharp
public interface IGeocodingProvider
{
    Task<GeocodeResult> GeocodeAsync(string query, GeocodeOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed record GeocodeResult(GeocodeOutcome Outcome, GeoPoint? Point, string? ProviderStatus);

public sealed record GeocodeOptions
{
    public string? Region { get; init; }
    public string? Language { get; init; }
}

public enum GeocodeOutcome
{
    Unspecified = 0,
    Found,
    NotFound,
    ProviderLimit,
    ProviderError,
}
```

- [ ] **Step 4: Implement `GoogleGeocodingProvider`**

Map `status`: `OK` → `Found`, `ZERO_RESULTS` → `NotFound`, `OVER_QUERY_LIMIT` and
`OVER_DAILY_LIMIT` → `ProviderLimit`, everything else and any non-success HTTP code → `ProviderError`.
Parse with `System.Text.Json`; take `results[0].geometry.location.{lat,lng}`.

- [ ] **Step 5: Run — expect PASS**

- [ ] **Step 6: Write the failing key-disclosure test**

```csharp
// Google takes the API key as a query parameter; there is no header form. Writing no log statement of
// our own is NOT sufficient: AddHttpClient attaches LoggingHttpMessageHandler, which logs
// "Sending HTTP request GET {uri}" at Information under System.Net.Http.HttpClient.*. The handler that
// leaks the key is one nobody wrote, so a test capturing only our own logger passes while the key is
// logged beside it.
[Fact]
public async Task The_api_key_never_reaches_any_log_including_the_http_clients_own()
{
    // _capture registers for EVERY category, so it sees System.Net.Http.HttpClient.* too. Capturing only
    // ILogger<GoogleGeocodingProvider> would pass while the key is logged beside it by a handler nobody
    // wrote. The stub primary handler keeps the request off the network while still routing it through
    // LoggingHttpMessageHandler, which is the thing under test.
    var services = new ServiceCollection()
        .AddLogging(b => { b.AddProvider(_capture); b.SetMinimumLevel(LogLevel.Trace); });
    services.AddThemiaGeoGoogle(o => o.ApiKey = "SECRET-KEY-VALUE");
    services.AddHttpClient(GoogleGeocodingProvider.HttpClientName)
        .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.OK, "{\"status\":\"ZERO_RESULTS\"}"));

    using var sp = services.BuildServiceProvider();
    await sp.GetRequiredService<IGeocodingProvider>().GeocodeAsync("x", null, default);

    Assert.DoesNotContain(_capture.AllMessages, m => m.Contains("SECRET-KEY-VALUE", StringComparison.Ordinal));
}
```

- [ ] **Step 7: Run to verify failure** — it must fail with the default registration, proving the handler
      logs the URI. If it passes before the fix, stop and report: the premise is wrong and the test is
      worthless.

- [ ] **Step 8: Suppress the handler's request-URI logging** in `AddThemiaGeoGoogle` —
      `.RemoveAllLoggers()` on the `IHttpClientBuilder`, or a replacement logging handler that strips the
      `key` parameter. Whichever is used, keep a comment saying why.

- [ ] **Step 9: Run — expect PASS**

- [ ] **Step 10: Fill both `PublicAPI.Unshipped.txt` files, add the projects to `Themia.sln`, build clean**

- [ ] **Step 11: Commit**

```bash
git add src/neutral/Themia.Geo src/neutral/Themia.Geo.Google tests/Themia.Geo.Google.Tests Themia.sln
git commit -m "feat(geo): geocoding contract and the Google provider"
```

---

## Task 3: DI, layering assertion and docs

**Files:**
- Create: `src/neutral/Themia.Geo/{GeoOptions.cs,DependencyInjection/GeoServiceCollectionExtensions.cs}`,
  `src/neutral/Themia.Geo/README.md`
- Test: `tests/Themia.Geo.Tests/LayeringTests.cs`

- [ ] **Step 1: Write the failing layering test**

```csharp
[Fact]
public void Themia_Geo_references_no_framework_package()
{
    var offenders = typeof(GeoPoint).Assembly.GetReferencedAssemblies()
        .Select(a => a.Name!)
        .Where(n => n.StartsWith("Themia.Framework.", StringComparison.Ordinal))
        .ToArray();

    Assert.Empty(offenders);
}
```

- [ ] **Step 2: Run — expect PASS immediately.** Nothing was added that would break it; this pins the
      constraint against a later change rather than fixing a present defect. Confirm it can fail by
      temporarily adding a `ProjectReference` to `Themia.Framework.Core`, watching it go red, then
      removing it.

- [ ] **Step 3: Implement `AddThemiaGeo`** — registers `GeoOptions` with `ValidateOnStart`. There is no
      provider to register here; a provider package adds its own.

- [ ] **Step 4: Write `README.md`** covering: the two distance formulas and when each is right; that
      `AroundMetres` is the SQL prefilter; that this package holds **no** POI store, gazetteer, name
      matching or fuzzy search, and why (both consumers asked for that boundary); that neither formula is
      ellipsoid-corrected.

- [ ] **Step 5: Build clean, run both test projects**

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Geo tests/Themia.Geo.Tests
git commit -m "feat(geo): service registration, layering assertion and package README"
```

---

## After this plan

`Themia.Geo` is complete but **not released**. The version bump, the `CHANGELOG.md` entry covering both
packages, and the architecture-overview correction are the AI plan's final task. Do not bump the version
here — one release, two packages.
