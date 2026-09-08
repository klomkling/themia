# Themia.Geo — design

**Status:** approved scope, not yet implemented.
**Target version:** `0.24.0`.
**Evidence:** coord #0115, answered by both consumers with production measurements. Every scope decision
below is something one or both of them asked for explicitly; the exclusions are things they asked us
**not** to ship.
**Supersedes:** the `Themia.Modules.Geo` row in `docs/themia-architecture-overview.md` §B. There is no
module — see §2.

---

## 1. What this is

Coordinate primitives and a geocoding seam. Pure computation plus one HTTP client.

It is **not** a POI store, **not** a gazetteer, and **not** a name resolver. Those hold Thai-language
data and per-app naming decisions, and both consumers said so independently and unprompted.

### The one-paragraph version of coord #0115

Both apps already extracted OSM independently — propertiezy has one `Pois` table (4,547 rows,
coordinates `NOT NULL`), ezy-assets has four typed tables (845 rows, 739 with coordinates) plus
`TransitLines`. Neither pushes to the other. Neither has a proximity query, PostGIS, or any distance
arithmetic on a search path. Neither has alias rows. What they both asked for is the same short list:
a geocoding provider, a validated point type, distance, and bounding boxes.

---

## 2. Package shape — and why there is no module

```
Themia.Geo          net8.0;net10.0   GeoPoint, GeoDistance, GeoBounds, IGeocodingProvider
Themia.Geo.Google   net8.0;net10.0   GoogleGeocodingProvider
```

**No `Themia.Modules.Geo`.** The architecture overview lists one, and it should not exist: there is no
tenant-scoped state, no table, no schema migration, no `IThemiaModule` lifecycle to run. Everything here
is a value type, a static calculation, or an HTTP call. A module wrapper would add a package whose only
content is DI registration that `AddThemiaGeo` already does.

This is worth stating because every other Phase 3 item did need one, and the pattern would otherwise be
copied without asking whether it applies.

`Themia.Geo` takes no dependency on `Themia.Framework.*` and none on a database. `Themia.Geo.Google`
adds `IHttpClientFactory` only.

---

## 3. `GeoPoint`

```csharp
public readonly record struct GeoPoint
{
    public double Latitude { get; }
    public double Longitude { get; }

    public GeoPoint(double latitude, double longitude);   // throws on out-of-range

    public static bool TryCreate(double latitude, double longitude, out GeoPoint point);
}
```

Latitude in `[-90, 90]`, longitude in `[-180, 180]`, both finite. `NaN` and infinity are rejected —
they propagate silently through every distance formula and produce a `NaN` result that compares false
against every threshold, so a proximity filter containing one degenerate point returns nothing and
reports no error.

**Stored as `double`, not `decimal`.** Both consumers store `decimal(9,6)` in their schemas, which is
right for storage: it is exact, and six decimal places is ~11 cm. But every trigonometric function in
.NET takes `double`, so a `decimal`-typed API would convert on entry and exit of every call. The
conversion belongs at the persistence boundary, in the app, not in the middle of the arithmetic.

`(0, 0)` is a valid point in the Gulf of Guinea and this type does not reject it. Callers treating
"no coordinate" as `(0, 0)` rather than a nullable have a bug this type cannot see; both consumers use
nullable columns or `NOT NULL`, so neither has it today.

---

## 4. Distance — two formulas, both shipped, named for what they are

```csharp
public static class GeoDistance
{
    /// Great-circle distance on a sphere. Correct at any separation.
    public static double HaversineMetres(GeoPoint a, GeoPoint b);

    /// Flat-earth approximation with a cosine correction for latitude.
    /// Cheaper; error grows with distance and with latitude.
    public static double EquirectangularMetres(GeoPoint a, GeoPoint b);
}
```

**Shipping only haversine would have meant propertiezy not using this package.** Their
`EducationFilter.IsWithinCampus` uses equirectangular *deliberately*, with the reason recorded in the
code: over the couple of kilometres it measures, at Thai latitudes, the difference from haversine is
centimetres. They told us they are happy with their own arithmetic and would consume the geocoding
provider and the bounding-box builder.

A package that offers only the formula an existing consumer already rejected, for a reason they wrote
down, is a package they will keep not using. Both ship, each documented with the case it is for:
equirectangular for radius filters under ~100 km, haversine when the separation is unbounded or the
result is shown to a user as a number.

**Neither is corrected for the WGS84 ellipsoid.** Haversine's sphere is an approximation too — up to
~0.5% against Vincenty. For "listings within 2 km" that is 10 m and irrelevant; for a distance
displayed to two decimal places it is not. Stated here so nobody later reports the sphere as a defect.

---

## 5. `GeoBounds`

```csharp
public readonly record struct GeoBounds
{
    public GeoPoint SouthWest { get; }
    public GeoPoint NorthEast { get; }

    public static GeoBounds AroundMetres(GeoPoint centre, double metres);
    public bool Contains(GeoPoint point);
}
```

`AroundMetres` is the SQL prefilter both consumers will need and neither has: a bounding box on indexed
`Latitude`/`Longitude` columns narrows the candidate set with a plain range scan, and the exact distance
is then computed on what survives. Without it, "listings within N metres" is a full scan with a
trigonometric function per row.

**Longitude degrees shrink with latitude** — `metres / (111320 * cos(lat))`. Getting this wrong is the
classic bug: a box computed with a fixed degree-per-metre is too narrow east-west at any latitude away
from the equator, so the prefilter silently drops rows that are inside the radius. At Bangkok's ~13.7°N
the error is ~3%; the test asserts the box contains a point placed exactly at the radius due east.

**`cos(latitude)` is clamped.** It approaches zero at the poles and the division blows up; the
implementation clamps the divisor so a request near a pole returns a wide box rather than infinity or
`NaN`. No Thai input reaches that region, which is exactly why an unguarded division would never be
noticed until something fed it a bad point.

`Contains` does **not** handle a box crossing the antimeridian, and `AroundMetres` does not produce one
for any Thai input. Documented rather than solved: solving it correctly means splitting into two boxes
and changing the return type for every caller, to serve a case neither consumer has.

---

## 6. `IGeocodingProvider`

```csharp
public interface IGeocodingProvider
{
    Task<GeocodeResult> GeocodeAsync(string query, GeocodeOptions? options = null, CancellationToken ct = default);
}

public sealed record GeocodeResult(GeocodeOutcome Outcome, GeoPoint? Point, string? ProviderStatus);

public enum GeocodeOutcome
{
    Unspecified = 0,
    Found,
    NotFound,        // the provider answered; nothing matched
    ProviderLimit,   // quota or rate limit — the caller should stop, not retry the batch
    ProviderError,   // transport or 5xx — a retry may work
}
```

**`Unspecified = 0` is reserved and rejected**, as in every other Themia enum. `SequenceEngine.Postgres = 0`
in `0.22.0` made `default` a valid value and `Enum.IsDefined` returned true for a field nobody had set.

**`ProviderLimit` is separate from `ProviderError` because the correct response differs.** A batch
backfill that treats a quota rejection as a transient error will retry it for every remaining row and
burn the rest of the day's allowance on calls that cannot succeed. The existing `ProjectGeocodingService`
already distinguishes these; keeping the distinction in the contract is what stops a caller collapsing
them.

```csharp
public sealed record GeocodeOptions
{
    /// ISO 3166-1 alpha-2 region bias, e.g. "TH". Biases results; does not restrict them.
    public string? Region { get; init; }

    /// Language for the provider's own response text, e.g. "th".
    public string? Language { get; init; }
}
```

Nothing else: parameters that vary per provider stay in the provider's own options type.

**No failover between providers, unlike `Themia.AI`.** The asymmetry is deliberate and worth stating
because the two specs land together. Only one geocoding provider ships, geocoding runs in a background
backfill where a caller can simply stop and resume on the next run, and the budget that `ProviderLimit`
protects is a monthly quota the caller owns (§7) rather than a per-minute rate limit that clears in
seconds. `Themia.AI` fails over because its work sits behind a publish button and its limits are
per-minute; neither is true here.

### `Themia.Geo.Google`

`GoogleGeocodingProvider` over `IHttpClientFactory`, ported from `ProjectGeocodingService.TryGeocodeAsync`.
Maps Google's `status` field onto `GeocodeOutcome`: `OVER_QUERY_LIMIT` → `ProviderLimit`, `ZERO_RESULTS`
→ `NotFound`, `OK` → `Found`, everything else → `ProviderError`.

### The API key is in the query string, and `IHttpClientFactory` logs the URL by itself

Google's Geocoding API takes the key as a query parameter; there is no header form. So the key is in
every request URI.

**Writing no log statement of our own is not sufficient.** `AddHttpClient` attaches
`LoggingHttpMessageHandler`, which logs `Sending HTTP request GET {uri}` at Information under the
`System.Net.Http.HttpClient.*` category. An implementer who follows a "do not log the URL" instruction
to the letter still ships a key into the host's logs, because the handler that logs it is one nobody
wrote.

So the registration must suppress or redact that handler's output — `RemoveAllLoggers()` on the client
builder, or a replacement handler that strips the `key` parameter before logging.

**The test must assert against the `System.Net.Http.HttpClient` log category, not only our own logger.**
A test that captures just `ILogger<GoogleGeocodingProvider>` passes with the key being logged by the
handler beside it, which is the failure this is guarding.

---

## 7. Deliberately not in this package

Each of these was considered and rejected for a stated reason. They are listed so a later reader does
not re-add one as an obvious omission.

| not shipping | why |
| --- | --- |
| **`IPoiLookup`** | Proposed in #0115 and rejected by ezy-assets: their store fans a category across four tables, propertiezy's filters one column. `FindByName(name, category)` maps onto one and not the other. Two consumers, two shapes, no useful common interface. |
| **Name matching, fuzzy search, transliteration, normalisation** | Both consumers asked us not to. The problems are Thai-specific and unsolved on both sides: RTGS `Lat Phrao` against the commonly written `Ladprao`; the abbreviation mark in `จรัญฯ 13` versus `จรัญสนิทวงศ์ 13`, which share three characters; `NameEn` holding transliteration for one row and translation for the next with nothing distinguishing them. None of that is geometry, and a neutral package would get it wrong for both. |
| **API quota tracking** | The existing service tracks a monthly Google budget in ~40 lines against its own table. Making that neutral means a new table and a dialect per engine — the full `Sequences`/`Audit` ceremony — for a counter that exactly one consumer needs. `GeocodeOutcome.ProviderLimit` gives the caller what it needs to stop; the budget is theirs. |
| **Batch backfill orchestration** | "Find rows with no coordinate, geocode, write back" needs an abstraction over *which rows* and *what a row is*, which is the 80 lines of domain SQL in the source. The interface an app would implement is larger than the code it would save. |
| **PostGIS or a spatial index** | Neither consumer has PostGIS, and both operate over one metro area. A bounding-box prefilter plus one of §4's formulas is proportionate. PostGIS becomes justified with polygons at scale or nearest-neighbour ordering; neither exists today. |
| **`GeoPolygon` / point-in-polygon / distance-to-boundary** | An earlier draft of this spec ported ezy-assets' `AreaBoundary` (223 lines) on the reasoning that it is pure geometry with nothing domain-specific in it. That reasoning is correct and it is still the wrong call: **neither consumer asked for it.** Both request lists in #0115 are the same four items — geocoding provider, point type, distance, bounding boxes. "This code could be shared" is not "someone needs it shared", and the one app that has it has it working. Porting it would mean asking ezy-assets to delete a working file and depend on us for it, which nobody proposed. Additive later, if either side asks. |
| **Reverse geocoding** | Nobody asked. Additive later. |

---

## 8. What this does not fix, and should not be mistaken for

**A listing does not reliably carry a coordinate**, so proximity search over listings is an edge case
today, not a feature this package enables:

- propertiezy has a project-coordinate fallback (a `COALESCE` for member listings whose typed project
  matched a catalogued one) — but not for unmatched projects, listings with no project, or anything
  arriving from ezy-assets.
- **ezy-assets has no fallback at all.** `MarketplaceDeliveryService.cs:203` builds the snapshot with
  `Latitude: property.Latitude` straight through, so a listing carries a coordinate only if the agent
  set one. `Projects` has had `Latitude`/`Longitude` since March and this path does not read them.

**The cheapest thing that changes this is one `COALESCE` in ezy-assets, not anything in `Themia.Geo`.**
Recorded here so the package is not credited with enabling a feature that is still blocked upstream of
it.

Also outstanding, and theirs: ezy-assets' POI adoption drops elements silently — `stations` fetched 269
and stored 200, with `LastError` empty because only database rejections are written there and the four
deliberate skip categories are counted and discarded. Any row count read through a future POI seam is
post-adoption and lossy in a way that is currently invisible to them too.

---

## 9. Testing

No containers. Everything here is computation or one HTTP call, and a database would prove nothing that
a unit test does not.

- **Distance against published reference pairs**, not against the other formula. Comparing haversine to
  equirectangular tells you they agree, which they do even when both are wrong; the expected value has
  to come from outside the implementation.
- **The east-west bounding-box case at Thai latitude.** A point placed exactly `r` metres due east of
  the centre must be inside `AroundMetres(centre, r)`. This is the assertion that fails when the
  longitude scaling forgets `cos(latitude)`, and a north-south-only test passes with that bug present.
- `GeoPoint` rejects `NaN`, infinity, and out-of-range values on both axes.
- **Every `GeocodeOutcome` maps from a payload captured from a real Google response**, `ProviderLimit`
  included. Capture each one once, store it as a fixture file, and record the date and endpoint it came
  from. A hand-written payload makes the expected value and the implementation share an author: if the
  real status string for a quota rejection is not what was assumed, the test passes and production takes
  the wrong branch. Any outcome with no captured payload behind it is marked unproven rather than
  quietly asserted.
- **The API key never reaches any log, including `System.Net.Http.HttpClient`'s own.** Capture that
  category, not only `ILogger<GoogleGeocodingProvider>` — the handler that logs the request URI is one
  nobody wrote, so a test scoped to our own logger passes while the key is logged beside it.
- `AroundMetres` near a pole returns a finite box rather than `NaN` or infinity.

---

## 10. Decisions — do not relitigate

1. No `Themia.Modules.Geo`. No tenant state, no schema, nothing for a module to own.
2. `GeoPoint` is `double`; the `decimal` conversion belongs at the app's persistence boundary.
3. Both distance formulas ship. Equirectangular is not a lesser haversine — it is what an existing
   consumer deliberately chose, and omitting it means they keep their own.
4. `GeoBounds.AroundMetres` scales longitude by `cos(latitude)`.
5. No `GeoPolygon`. Pure geometry, but nobody asked and its owner has it working — §7.
6. `GeocodeOutcome` reserves `0` and separates `ProviderLimit` from `ProviderError`.
7. No `IPoiLookup`, no name matching, no quota tracking, no backfill orchestration — §7 for each reason.
8. The Google provider must suppress `IHttpClientFactory`'s own request-URI logging; the key is a query
   parameter and there is no header form.
8. `Themia.Geo` references no `Themia.Framework.*` package and no database; asserted by a test.
