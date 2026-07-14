# Themia.Storage — permanent public URLs (coord #0022)

**Date:** 2026-07-14
**Status:** approved, ready for an implementation plan
**Target version:** 0.8.8 — **(breaking)** for direct implementors of `IStorageProvider`
**Request:** coord #0022, from `propertiezy`

## Why

Propertiezy filed #0022 as "Themia has no storage/blob abstraction". That premise was wrong —
`Themia.Storage`, `Themia.Storage.S3` and `Themia.Modules.Storage` all ship (0.8.7 on nuget.org) with
`IStorageProvider`, tenant scoping, quota, validation and presigned uploads. The parts of the request
that already exist are **not** rebuilt here.

What genuinely does not exist is a **permanent public URL**. Themia has only
`GetPresignedUrlAsync` — a *signed, expiring* URL — and `LocalStorageProvider` returns a **relative**
`Uri` that cannot be hot-linked cross-origin at all.

**Presigned ≠ public.** A presigned URL is a time-boxed capability handed to one caller. A public URL
is a permanent, world-readable address. An expiring URL breaks every consumer that matters for a
marketplace:

- an **OG/Twitter image** in a listing shared to LINE or Facebook — the share is permanent, the URL is not;
- a **crawler** (Google Images) that re-fetches next month and gets a 403;
- a **CDN**, defeated because every render mints a fresh URL, churning the cache key — which also makes
  the consumer's SSR pages uncacheable.

The tenant-isolation consequence is accepted with eyes open: a published listing photo *is*
world-readable — that is the product, not a leak. The control is not the URL; it is **which objects
enter the public space at all**.

### The bug this is really fixing

`ezy-assets`' `LocalFileStorage.ResolvePublicBasePath` force-prefixes `/`
(`LocalFileStorage.cs:54`), so it is *structurally incapable* of emitting an absolute URL. Because
`PropertyPhotos.Url` stores the URL **resolved at upload time**, every photo uploaded under Local has a
relative URL frozen in the database permanently — switching that tenant to S3 later does not rewrite
those rows. This design makes that class of bug unrepresentable (see "Key, not URL").

## Decisions

### 1. Visibility is a property of the container, not of the object

Forced by the backends, not chosen:

- **Cloudflare R2 has no per-object ACL.** Public access is per-bucket (custom domain / `r2.dev`).
- **S3 Object Ownership defaults to *bucket owner enforced*, which disables object ACLs entirely.**

So a `StoragePutOptions.IsPublic` flag would be a leaky abstraction that silently no-ops on both real
targets. Instead: **two containers** — a private one and a public-read one — and `Visibility` is
implemented by **routing to a different container, never by setting an ACL**.

The **provider** (backend technology) is still chosen once via DI — `UseLocal` **or** `UseS3` **or**
`UseR2`, exactly as today. What changes is that the single registered provider now addresses **two
containers**.

### 2. The physical key carries its own container address

Rejected alternatives:

- **Pass visibility on every provider call** (`GetAsync(key, visibility)`) — invasive, every method grows
  a parameter, and a wrong value fails silently as "not found" in the wrong container.
- **Two provider instances in keyed DI** — the registration seam currently throws when a second backend
  is registered (`StorageServiceCollectionExtensions.cs:123`), and every consumer resolving
  `IStorageProvider` would have to know which one it got.

Chosen: `StorageScope.PhysicalKey(tenant, key, visibility)` yields

| Visibility | Physical key           |
|------------|------------------------|
| `Private`  | `{tenant}/{key}`       |
| `Public`   | `public/{tenant}/{key}` |

The provider maps the leading segment to a container and strips it. The key is **self-addressing**, so
`GetAsync` / `StatAsync` / `ExistsAsync` / `DeleteAsync` / `GetPresignedUrlAsync` keep working
**unchanged** — no new parameter, no lookup.

Two properties of this shape carry their weight:

- **Private stays unprefixed**, so every blob already written keeps its exact key. **Nothing existing
  moves.** This is the back-compat proof, and a test pins it.
- `public` becomes a **reserved first segment**, and a tenant whose id is `public` is rejected at key
  construction — the same guard `StorageScope` already applies to `_platform` (`StorageScope.cs:24`).
  Extending an existing pattern, not inventing one.

### 3. Configured `PublicBaseUrl`; never request-derived

Permanent URLs are resolved from configuration only. A request-derived origin breaks in exactly the
cases that matter: **background jobs have no `HttpContext`**, and behind a proxy or CDN you capture the
*internal* origin and freeze it.

A deliberate asymmetry: the existing `ToAbsolute(httpRequest, prefix, url)` helper
(`StorageEndpoints.cs:38`) keeps absolutizing **presigned upload** URLs from the incoming request —
those are ephemeral, minted and consumed within minutes by the browser that just called.
**Ephemeral may be request-derived; permanent may not.**

### 4. Key, not URL — enforced by omission

No API in this design returns a URL from a write. `PutAsync` returns `StoredObject` (id, key, size,
content type); `CompleteUploadAsync` likewise. A consumer **cannot** persist a resolved URL from a put,
because it never receives one. The URL exists only at read time, resolved from current config — which
makes a CDN swap or domain change a **config edit instead of a data migration**.

### 5. Visibility is mutable, via a real move

Flipping visibility physically moves bytes between containers. `SetVisibilityAsync` is ordered
**copy → update row → delete source**:

- crash between copy and row-update → object still readable at its **old** key;
- crash between row-update and delete → an **orphan** in the old container, collected by the reconcile
  sweep the module already anticipates (`TenantStorage.cs:188`).

**No ordering loses bytes.** A no-op flip (already that visibility) returns without touching the backend.

## Design

### Neutral core — `Themia.Storage`

```csharp
public enum StorageVisibility { Private, Public }

// StoragePutOptions gains Visibility, defaulting to Private:
public readonly record struct StoragePutOptions(
    string ContentType,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool Overwrite = true,
    StorageVisibility Visibility = StorageVisibility.Private);

// IStorageProvider gains exactly two members; everything else is untouched:
Uri GetPublicUrl(string key);   // sync — pure config + key composition, no I/O
Task MoveAsync(string sourceKey, string destinationKey, CancellationToken ct = default);
```

`GetPublicUrl` throws `InvalidOperationException` when the key is not in the public space, or when no
public container is configured. **It never returns a URL that would 403 at render time** — a URL that
looks right and fails in the browser is the worst of the available outcomes.

`MoveAsync`: server-side `CopyObject` + `DeleteObject` for S3/R2 (bytes never transit the API); a file
move for Local.

Provider options gain a public container and an absolute base URL:

| Provider              | Private      | Public              | Public base URL |
|-----------------------|--------------|---------------------|-----------------|
| `LocalStorageOptions` | `RootPath`   | `PublicRootPath`    | `PublicBaseUrl` |
| `S3StorageOptions`    | `BucketName` | `PublicBucketName`  | `PublicBaseUrl` |
| `R2StorageOptions`    | `BucketName` | `PublicBucketName`  | `PublicBaseUrl` |

`PublicBaseUrl` **must be absolute**, validated in `Validate()` at **startup** — a relative value throws
rather than silently reproducing the ezy-assets bug. It is the bucket's custom domain for S3/R2, and the
app's own origin for Local.

### Module — `Themia.Modules.Storage`

`StorageObject` gains a `Visibility` column. A FluentMigrator migration adds it defaulting to `Private`,
which is correct for every object that exists today. (Schema is FluentMigrator-owned; no
`dotnet ef migrations add`.)

Reading it is **free**: every module read and delete already fetches the row before touching the provider
(`TenantStorage.cs:157`, `:178`). So `PhysicalKey(tenant, key)` becomes
`PhysicalKey(tenant, key, row.Visibility)` in `GetAsync`, `ExistsAsync`, `DeleteAsync` and
`GetDownloadUrlAsync` — one line each, **zero new queries**.

```csharp
Task<Uri> GetPublicUrlAsync(string key, CancellationToken ct = default);
Task SetVisibilityAsync(string key, StorageVisibility visibility, CancellationToken ct = default);
```

`GetPublicUrlAsync` looks up the row and **throws `StorageNotPublicException` for a private object**, so
the failure lands at the call site rather than in a browser. A missing object throws too: asking for the
public URL of a nonexistent key is a caller bug.

`GetUploadUrlAsync` gains a `StorageVisibility` parameter (default `Private`) so a presigned upload lands
**directly in the right container** — otherwise every large video upload would need a move immediately
after. `CompleteUploadAsync` needs no signature change; it reads visibility off the pending row.

Quota is unchanged and deliberately counts **both** containers: public bytes are still the tenant's bytes.

### Serving public bytes

**The `public/` prefix is a routing marker, not part of the address.** It selects the container and is
then **stripped** — it appears in neither the stored object key nor the public URL. A public object with
logical key `listings/42/hero.jpg` in tenant `t1` has physical key `public/t1/listings/42/hero.jpg`, is
stored in the public container at `t1/listings/42/hero.jpg`, and is served at
`{PublicBaseUrl}/t1/listings/42/hero.jpg`. The prefix never leaks to a URL.

**S3/R2:** nothing to serve. `GetPublicUrl` composes `PublicBaseUrl + "/" + <key with the prefix
stripped>`; bytes come from the bucket's custom domain or the CDN in front of it. **Themia is never in
the request path** — the entire point for crawlers, OG unfurls and CDN caching.

**Local:** the module's endpoint group gains one route.

```
GET {mount}/public/{**key}   →  no auth, no token, streams from PublicRootPath
                                {key} is the stripped key, e.g. t1/listings/42/hero.jpg
```

For Local, `PublicBaseUrl` is therefore the app's absolute origin **plus this mount path** (e.g.
`https://api.example.com/storage/public`). The mirror of the existing `_local/get` route, minus the
signature — a public object is public by definition, so there is no token to verify. Content type comes from the sidecar via `provider.GetAsync`
(Local stores content types under `{root}/content-types`). Keys pass through
`StorageKey.NormalizeAndValidate` and resolve strictly under `PublicRootPath`, so traversal cannot escape
into the private root. It emits `Cache-Control: public, max-age=…` (configurable, default one day) — the
deliberate opposite of the dashboards' `no-store`, and correct: these bytes are not sensitive.

Local-serving is **production-supported**, not dev-only: `ezy-assets`' base `appsettings.json` sets
`"Provider": "Local"`, and its production `Storage__Provider` is a Coolify env var that may well be unset.

## Error handling

| Condition | Behaviour |
|---|---|
| `GetPublicUrl` on a private key | `InvalidOperationException` at the call site |
| `GetPublicUrl` with no public container configured | `InvalidOperationException` |
| `GetPublicUrlAsync` on a private object (module) | `StorageNotPublicException` |
| `GetPublicUrlAsync` on a missing object | throws — caller bug |
| `PublicBaseUrl` not absolute | throws at **startup** in `Validate()` |
| Tenant id `public` | rejected in `StorageScope`, as `_platform` is today |
| Crash mid-`SetVisibilityAsync` | object readable at one key or the other; worst case an orphan blob |

## Testing

- **Key addressing:** public keys get the prefix; private keys are **byte-identical to today** (the
  back-compat proof — existing blobs must not move); tenant id `public` is rejected.
- **`GetPublicUrlAsync`:** throws for a private object; returns an absolute URL for a public one; the URL
  changes with config alone, with no data change.
- **`SetVisibilityAsync`:** moves both directions; updates the row; leaves the old key empty; a simulated
  failure of the final delete still leaves the object readable at the new key.
- **Local public route:** serves without auth; emits the sidecar content type and `Cache-Control`; rejects
  traversal; **cannot reach a private key**.
- **Migration:** existing rows default to `Private`.

## Out of scope

Each is real; none is this request.

- **Resumable / multipart upload** — explicitly deferred by propertiezy. Ship the size-capped single
  presigned PUT first; add resumable only if Thai mobile uploads actually hurt. Building it
  speculatively is the expensive guess.
- **Transcoding** — a genuinely separate problem. The assumption is "store raw, serve raw", which holds
  only while the size cap is real.
- **Per-object ACLs** — impossible on R2/S3, as established above.
- **Already exists, not rebuilt:** the content-type- and size-restricted presigned PUT
  (`ITenantStorage.GetUploadUrlAsync` + `CompleteUploadAsync`, with quota reservation,
  `StorageModuleOptions.MaxObjectSizeBytes` default 100 MiB, and `AllowedContentTypes` enforced by
  `DefaultFileValidator`).

## Known caveat

**Making a public object private does not make it instantly unreachable.** A CDN keeps serving its cached
copy until the TTL expires, and crawlers may already hold copies. `SetVisibilityAsync` moves the origin
bytes; it cannot un-publish the internet. If "unpublish must be immediate", the answer is a shorter CDN
TTL or explicit cache invalidation — an app-side concern, not a storage-layer one.

## Open thread

`ezy-assets`' production `Storage__Provider` is set in Coolify and is **unverified**. If it is `Local`,
its existing photos carry relative URLs frozen at upload time and media re-hosting becomes a V1
prerequisite rather than a P2 nicety.
