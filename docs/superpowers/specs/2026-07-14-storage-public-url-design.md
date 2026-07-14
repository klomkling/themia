# Themia.Storage — permanent public URLs (coord #0022)

**Date:** 2026-07-14 (rev 2, 2026-07-15 — visibility made immutable; `MoveAsync` deleted)
**Status:** approved, ready for an implementation plan
**Target version:** 0.9.0 — **(breaking)** for direct implementors of `IStorageProvider`
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

### 5. Visibility is chosen at write time and is **immutable**

The first draft of this spec supported flipping visibility after write, via a `MoveAsync`
(copy → update row → delete source) with an orphan-blob failure mode and a reconcile sweep. **That
mechanism is deleted.** Every expensive part of the two-container design existed *only* to support the
flip — it was the move machinery that was costly, not the two containers.

Interrogating whether the flip ever legitimately happens, it does not:

- **Private → public** is unnecessary. A photo on a draft listing does not need to be private: keys are
  unguessable GUIDs, which is what every CMS does (WordPress uploads, Notion). The residual "leak" is that
  someone who *guesses a GUID* sees a photo that becomes public within days anyway — not a threat worth
  paying a distributed copy-with-orphans mechanism to prevent.
- **Public → private** is pointless after the fact. Once a listing photo has been crawled by Google Images
  and cached at a CDN edge, making the origin 403 changes nothing real.

So: **each mechanism is used where it is actually right, and nothing ever converts between them.** Public
objects are born in the public container and served **direct from the bucket**. Private objects (invoices,
receipts, ID documents — genuinely different data) live in the private container and are **route-served**
through the authenticated endpoint, at low volume.

**Consequence:** a re-put of an existing key with a *different* `Visibility` **throws**. It does not
silently orphan the old blob, and it does not silently ignore the caller's argument. There is exactly one
way bytes enter a container, and no way to move them between containers.

Adding a move later is purely **additive and non-breaking**, so nothing is foreclosed by leaving it out.

### 6. Why public bytes are served direct-from-bucket, not through a route

The tempting simplification is to route-serve public bytes too (a CDN in front of the app origin), which
would collapse visibility into a plain DB column and delete the second container entirely. It is rejected
for a reason that is **not** the egress bill:

Costed at Propertiezy's scale — ~10k listings × ~20 photos ≈ 200k objects ≈ 60 GB stored, ~400 GB/month
delivered, and immutable GUID-keyed images hitting 90–95% at the CDN → only ~20–40 GB/month of origin
egress — the bill is a rounding error, and route-serving would be fine.

The problem is **topology**. `ezy-assets`' API runs on a **homelab behind residential upload bandwidth**
(Coolify, `api.asetix.com`), and Propertiezy's deployment target may land somewhere similar. Pushing a
public marketplace's image traffic — plus Google Images crawls, plus an OG-scraper fetch on every
LINE/Facebook share — through the app origin is an **availability risk, not a cost one**: a crawl burst or
a cold cache after a purge saturates home upload and takes down the same API that serves ingest and the
agent app. That is a self-inflicted DoS surface, and **CDN hit ratio is precisely what you cannot rely on
during the bursts that hurt.**

Direct-from-bucket keeps the app out of the byte path entirely, which is the whole point.

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

// IStorageProvider gains exactly ONE member; everything else is untouched:
Uri GetPublicUrl(string key);   // sync — pure config + key composition, no I/O
```

`GetPublicUrl` throws `InvalidOperationException` when the key is not in the public space, or when no
public container is configured. **It never returns a URL that would 403 at render time** — a URL that
looks right and fails in the browser is the worst of the available outcomes.

There is **no `MoveAsync`** (see decision 5). A general-purpose move on the neutral provider would also
have been a tenant-isolation footgun: it takes two arbitrary physical keys at a layer with no tenant
awareness, so it could move a blob from one tenant's prefix into another's, and the isolation analyzer
does not cover it. Not adding it is the safer surface as well as the smaller one.

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
```

`GetPublicUrlAsync` looks up the row and **throws `StorageNotPublicException` for a private object**, so
the failure lands at the call site rather than in a browser. A missing object throws too: asking for the
public URL of a nonexistent key is a caller bug.

There is no `SetVisibilityAsync` (decision 5). `PutAsync` on an **existing** key whose stored visibility
differs from `options.Visibility` **throws** — the alternatives are both silent failures: writing at the
new visibility orphans the old blob, and writing at the old one ignores the caller's argument, leaving the
app believing it published a photo that is still private.

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

**It is mapped in the ungated `transfer` group, never the returned broker group.** 0.8.8 established this
seam: the group returned by `MapThemiaStorageEndpoints` is what adopters gate with
`.RequireAuthorization()`, so a public route mapped inside it would **401 in an `<img>` tag** — a public
URL that fails at render time, the worst of the available failure modes because it *looks* right. A test
must pin it: map the group, call `.RequireAuthorization()`, assert the public route still serves 200.

For Local, `PublicBaseUrl` is therefore the app's absolute origin **plus this mount path** (e.g.
`https://api.example.com/storage/public`). Startup validation asserts it is absolute **and** that it ends
with the mount path — an otherwise-valid base URL missing the mount segment passes an "is it absolute?"
check and then 404s on every image.

The route is the mirror of the existing `_local/get`, minus the signature — a public object is public by
definition, so there is no token to verify. Content type comes from the sidecar via `provider.GetAsync`
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
| `PutAsync` on an existing key with a different `Visibility` | throws — visibility is immutable |
| `PublicBaseUrl` not absolute, or missing the mount path (Local) | throws at **startup** in `Validate()` |
| Tenant id `public` | rejected in `StorageScope`, as `_platform` is today |

Note what is **absent**: there is no partial-failure state anywhere in this design. Because visibility is
immutable, no operation ever spans two containers, so there is no half-moved object, no orphan blob and
no reconcile sweep to write. That is the whole dividend of decision 5.

## Testing

- **Key addressing:** public keys get the prefix; private keys are **byte-identical to today** (the
  back-compat proof — existing blobs must not move); tenant id `public` is rejected.
- **`GetPublicUrlAsync`:** throws for a private object; returns an absolute URL for a public one; the URL
  changes with config alone, with no data change.
- **Immutability:** a re-put of an existing key with a different `Visibility` throws (and writes nothing).
- **Local public route:** serves **while the broker group has `.RequireAuthorization()` applied** — the
  0.8.8 lesson, and the test that would have caught it; emits the sidecar content type and `Cache-Control`;
  rejects traversal; **cannot reach a private key**. Plus the other half: the broker routes still 401, so a
  later change cannot ungate everything to make the public test pass.
- **Startup validation:** a relative `PublicBaseUrl`, and (for Local) one missing the mount path, both throw.
- **Migration:** existing rows default to `Private`.

## Out of scope

Each is real; none is this request.

- **Resumable / multipart upload** — explicitly deferred by propertiezy. Ship the size-capped single
  presigned PUT first; add resumable only if Thai mobile uploads actually hurt. Building it
  speculatively is the expensive guess.
- **Transcoding** — a genuinely separate problem. The assumption is "store raw, serve raw", which holds
  only while the size cap is real.
- **Per-object ACLs** — impossible on R2/S3, as established above.
- **Changing an object's visibility after write** (`MoveAsync` / `SetVisibilityAsync`) — deleted in
  decision 5. Purely additive to add later if a consumer ever genuinely needs it.
- **Already exists, not rebuilt:** the content-type- and size-restricted presigned PUT
  (`ITenantStorage.GetUploadUrlAsync` + `CompleteUploadAsync`, with quota reservation,
  `StorageModuleOptions.MaxObjectSizeBytes` default 100 MiB, and `AllowedContentTypes` enforced by
  `DefaultFileValidator`).

## Known caveat

**A public object cannot be un-published.** Deleting it removes the origin bytes, but a CDN keeps serving
its cached copy until the TTL expires, and crawlers may already hold their own copies. This is *why*
visibility is immutable rather than a limitation of it: a public→private flip would have offered the
*illusion* of un-publishing while changing nothing real. If "unpublish must be immediate", the honest
answers are a shorter CDN TTL or explicit cache invalidation — app-side concerns, not storage-layer ones.

## Resolved (was an open thread)

`ezy-assets`' production `Storage:Provider` **is `Local`** — but its uploads are **already public and
unauthenticated**: `app.UseStaticFiles(…)` (`Program.cs:589`) is registered *before*
`UseRouting`/`UseAuthentication`/`UseAuthorization` (615/618/625), so it short-circuits before any auth
runs, and the anonymous public web-proposal page already renders those images. Hot-linking works today, so
**this request does not go critical** — media re-hosting stays deferred, and Propertiezy V1 hot-links
ezy-assets' photos without touching `Themia.Storage` at all. **V1 volume through this package is zero**,
which is the honest reason there is no urgency here.

The residual gap is app-side and owned by them: `LocalFileStorage` emits a *relative* `/uploads/…` path, so
their snapshot builder needs a `ValidateOnStart` `Marketplace:PublicMediaBaseUrl` key to absolutize it.
