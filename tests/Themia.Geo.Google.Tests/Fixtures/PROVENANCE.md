# Themia.Geo.Google test fixtures

Every file here is the *exact* body a stub handler feeds `GoogleGeocodingProvider`, so it is
strict JSON with no comment header. Provenance lives here instead, one section per fixture — the
provider parses with `System.Text.Json` defaults, which reject comments, exactly as the real
endpoint's bytes would be parsed.

## `google-error.json`

Captured 2026-09-07 via: curl "https://maps.googleapis.com/maps/api/geocode/json?address=Bangkok&key=INVALID_KEY_FOR_FIXTURE_CAPTURE" A genuine live-captured Google response — no valid API key was needed or used, since a deliberately invalid key reliably produces REQUEST_DENIED. This is a real call to the real endpoint, not a hand-written payload.

## `google-ok.json`

Derived from the production parser in ezy-assets/src/EzyAssets.Infrastructure/Geo/ProjectGeocodingService.TryGeocodeAsync (reads `status` as a string compared to "OK"; `results` as a JSON array checked for non-zero length; and results[0].geometry.location.{lat,lng}), cross-checked against Google's published Geocoding API response format — NOT captured from a live call. If a later real capture's shape differs from this one (e.g. a field the production parser assumes is present is actually absent), that is a finding about the shipped ezy-assets service too, since it reads the same path.

## `google-over-query-limit.json`

UNPROVEN: from docs, not captured. Deliberately exhausting a real Google Geocoding quota to obtain this fixture is not reasonable, so this body is copied from Google's published status documentation rather than a live response. If a later real capture's `status` string differs from "OVER_QUERY_LIMIT", that is a finding about the shipped ezy-assets ProjectGeocodingService.cs too — its TryGeocodeAsync compares against the same literal.

## `google-zero-results.json`

Derived from the production parser in ezy-assets/src/EzyAssets.Infrastructure/Geo/ProjectGeocodingService.TryGeocodeAsync (reads `status` as a string and compares it case-insensitively; a non-"OK" status short-circuits before `results` is inspected further), cross-checked against Google's published Geocoding API response format — NOT captured from a live call. If a later real capture's `status` string differs from "ZERO_RESULTS", that is a finding about the shipped ezy-assets service too, since it compares against the same literal.
