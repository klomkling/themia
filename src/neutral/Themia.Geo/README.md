# Themia.Geo

Neutral coordinate primitives: a validated lat/lng point (`GeoPoint`), two great-circle distance
formulas (`GeoDistance`), and a metres-radius bounding box for SQL prefiltering (`GeoBounds`), plus the
`IGeocodingProvider` contract that a separate package (e.g. `Themia.Geo.Google`) implements.

Pure computation: no HTTP, no credentials, no I/O, no database, no framework dependency. Targets
`net8.0` and `net10.0`.

## No `AddThemiaGeo`

There is nothing in this package to register. `GeoPoint`, `GeoDistance` and `GeoBounds` are a struct
and two static classes — there is no service, no configurable constant, and no state. `IGeocodingProvider`
has no implementation here; a provider package registers itself (`Themia.Geo.Google`'s
`AddThemiaGeoGoogle` also registers `IGeocodingProvider`, so nothing upstream needs to). Adding a DI
extension and an options type with no field anything reads would be ceremony, not a seam — see
`Themia.Storage` for the same call on a package that is contracts plus one in-package implementation.
If a second neutral concern ever needs configuring here, add `AddThemiaGeo` then, for that reason.

## Distance: which formula, and neither is ellipsoid-corrected

```csharp
var metres = GeoDistance.HaversineMetres(a, b);
var approx = GeoDistance.EquirectangularMetres(a, b);
```

Both treat the Earth as a sphere (the IUGG mean radius, 6,371,008.8 m). Neither corrects for the
Earth's actual ellipsoid shape (WGS-84's ~0.3% flattening) — that is a deliberate simplification, not an
oversight. It matters at global scale (a handful of kilometres of error over an antipodal distance) and
does not matter for a metro-area radius filter, which is the whole intended use here. If a consumer ever
needs ellipsoid-corrected distance (surveying, aviation-grade positioning), that is Vincenty's formula or
a geodesy library — out of scope for this package.

Between the two:

- **`EquirectangularMetres`** — a flat-projection approximation. Cheap (one `cos`, one `sqrt`, no
  `asin`), and accurate only over short distances — a few kilometres, which covers a metro-area radius
  filter under roughly 100 km. Use it when you are filtering rows against a fixed radius and the number
  itself is never shown to anyone.
- **`HaversineMetres`** — accurate at any distance. Use it whenever the separation is unbounded, or
  whenever the computed distance is a number a user will see (a "3.2 km away" label) rather than only a
  yes/no filter — the extra `asin` is not worth trading accuracy for cents at that point.

## Bounding boxes: the SQL prefilter, not the answer

```csharp
var box = GeoBounds.AroundMetres(centre, metres: 5_000);
// WHERE latitude BETWEEN @swLat AND @neLat AND longitude BETWEEN @swLng AND @neLng
```

`GeoBounds.AroundMetres` sizes a box that fully *contains* a circle of the given radius — it is
deliberately wider than the circle at the corners. Use it as an indexed range scan to narrow candidate
rows before applying an exact `GeoDistance` formula to the survivors; do not treat "inside the box" as
"inside the radius" on its own.

## Deliberately not in this package

Each of these was considered and rejected for a stated reason, so a later reader does not helpfully
re-add one as an obvious omission.

- **POI storage, a gazetteer, or any `IPoiLookup` abstraction.** Proposed and rejected in coord #0115:
  the two consumer apps' POI stores do not share a shape (one fans a category across four tables, the
  other filters a single column), so a common interface would map cleanly onto neither.
- **Name matching, fuzzy search, and transliteration.** Both consumers explicitly asked us not to ship
  this (coord #0115). These are Thai-language problems, not geometry, and they are unsolved on both
  sides today: RTGS `Lat Phrao` against the commonly written `Ladprao`; the abbreviation mark in
  `จรัญฯ 13` versus `จรัญสนิทวงศ์ 13`, which share only three characters; a `NameEn` column holding
  transliteration for one row and translation for the next with nothing distinguishing them. A neutral
  package would get this wrong for both consumers, and the data each of those problems needs to solve
  correctly lives beside that consumer's own data, not in shared infrastructure.
- **API quota tracking.** A monthly-budget counter is ~40 lines against one table for the one consumer
  that needs it. `GeocodeOutcome.ProviderLimit` gives that caller what it needs to stop; owning the
  budget itself is the caller's job, not a neutral package's.
- **Batch backfill orchestration** ("find rows with no coordinate, geocode, write back"). The
  abstraction over *which rows* and *what a row is* would be larger than the ~80 lines of domain SQL it
  would replace.
- **PostGIS or a spatial index.** Neither consumer runs PostGIS, and both operate over one metro area; a
  `GeoBounds` prefilter plus a `GeoDistance` formula is proportionate. Reconsider only if polygons at
  scale or nearest-neighbour ordering are actually needed.
- **`GeoPolygon`, point-in-polygon, or distance-to-boundary.** Pure geometry with nothing
  domain-specific about it, and still rejected: neither consumer asked for it, and the one that has an
  equivalent (`AreaBoundary`) has it working today. "This could be shared" is not "someone needs it
  shared" — additive later, if either side asks.
- **Reverse geocoding.** Nobody asked. Additive later.

## `IGeocodingProvider`

```csharp
public interface IGeocodingProvider
{
    Task<GeocodeResult> GeocodeAsync(string query, GeocodeOptions? options = null, CancellationToken cancellationToken = default);
}
```

`GeocodeResult.Outcome` distinguishes `ProviderLimit` (quota/rate-limited — stop, do not retry the
batch) from `ProviderError` (transport failure or unexpected response — a retry may work). Collapsing
the two removes the caller's ability to tell "keep going" from "back off" apart. There is deliberately
no failover between providers here, unlike `Themia.AI`: geocoding runs in a background backfill that a
caller can simply stop and resume, and the quota `ProviderLimit` protects is a monthly budget the caller
owns, not a per-minute limit that clears in seconds.

Only one provider ships today: `Themia.Geo.Google`, whose `AddThemiaGeoGoogle`
(`Themia.Geo.Google.DependencyInjection`) registers `IGeocodingProvider` and its own
`GoogleGeocodingOptions` (the API key).
