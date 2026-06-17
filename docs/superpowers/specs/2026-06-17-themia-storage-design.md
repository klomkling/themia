# Themia.Modules.Storage — Object storage slice (0.5.3) Design

**Status:** Draft (2026-06-17)
**Scope:** The first slice of `Themia.Modules.Storage` — tenant-aware object storage over pluggable
backends, with a **Local** (filesystem) backend and an **S3 + Cloudflare R2** backend in one slice,
DB-backed object metadata + per-tenant quota, and seams for virus scanning and content validation.
The last un-specced Phase-1 cross-cutting module. Companion to
[`themia-architecture-overview.md`](../../themia-architecture-overview.md); follows the three-layer
pattern proven by `Themia.Exceptional.*` (neutral core) and the per-provider package topology of the
Dapper data engines.

---

## 1. Milestone context

Storage is **Phase 1 — core cross-cutting** (architecture overview §"Build order", line 261), the
peer of Scheduling ✅, ExceptionLogging ✅, and Identity ✅. It is **not** Phase 2 (Notifications /
Pdf / Export → 0.6.0+). It ships in the **0.5.x** line so Phase 1 stays inside pre-0.6; this first
slice is **0.5.3** (version and phase are separate counters per the release-strategy spec — Storage's
phase is fixed, the version is the next minor).

Donor sources (best-of merge): **ezy-assets** S3/Local storage, **Idevs** `CloudUploadStorage(+Options)`,
**PowerACC** ClamAV scan. ezy-assets is Themia's first consumer and uses S3/Local storage, so the S3
backend ships in this slice rather than being deferred.

Like Identity, Storage ships as dependency-ordered slices; this is the foundation slice.

## 2. Resolved decisions (do not relitigate)

These were locked during brainstorming on 2026-06-17:

1. **First slice = neutral core + Local + S3/R2 together.** R2 is S3-API-compatible (a configured
   endpoint + path-style), so it rides on the S3 backend for ~free, and the first consumer needs the
   cloud backend now.
2. **DB-backed metadata + quota.** The module tracks every object in a tenant-scoped table and
   enforces a per-tenant byte quota + max-object-size — not a stateless blob proxy.
3. **Virus scanning deferred.** A pluggable `IFileScanner` seam ships now with a no-op default; the
   ClamAV implementation is a later slice.
4. **Tenant isolation by key-prefixing** in a shared bucket/root (not per-tenant buckets). Works
   identically across Local/S3/R2, needs no per-tenant provisioning, and matches the framework's
   by-construction isolation ethos.
5. **S3/R2 in a separate package** (`Themia.Storage.S3`), keeping `AWSSDK.S3` out of the neutral core
   so Local-only consumers don't pull it — mirroring the per-engine Dapper package split.

## 3. Package layout & boundaries

Three packages, lower layers never depending on higher ones:

| Package | TFM | Depends on | Role |
|---|---|---|---|
| `Themia.Storage` | `net8.0;net10.0` | — (framework-free) | Neutral core: `IStorageProvider` abstraction + `LocalStorageProvider` + value types/options. |
| `Themia.Storage.S3` | `net8.0;net10.0` | `Themia.Storage`, `AWSSDK.S3` | `S3StorageProvider` (S3 + R2 + any S3-compatible endpoint, e.g. MinIO). |
| `Themia.Modules.Storage` | `net10.0` | `Themia.Storage`, `Themia.Framework.*`, `Themia.Data.Migrations` | `IThemiaModule`, tenant-aware `ITenantStorage`, DB metadata + quota, DI builder, opt-in endpoints. |

- **Scope guard:** only cross-cutting storage infrastructure enters Themia. Domain concepts (what a
  file *means* to an app — an invoice, an asset photo) stay in the consuming app's tables; Storage
  owns only the blob + its generic metadata/quota.
- **Neutral core is framework-free** (no tenancy, no data layer, no ASP.NET) so it stays reusable by
  net8 apps (e.g. PowerACC) exactly like `Themia.Quartz` / `Themia.Exceptional.*`.
- **Tenant awareness lives only in the module.** The core deals in opaque keys; the module prepends
  the tenant prefix.

## 4. Neutral core — `IStorageProvider`

Tenant-agnostic, opaque string keys, stream-based:

```csharp
public interface IStorageProvider
{
    Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken ct = default);
    Task<StorageReadResult?> GetAsync(string key, CancellationToken ct = default);           // null when absent
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);                            // idempotent
    Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken ct = default);
}
```

- `StoragePutOptions`: `ContentType`, optional `Metadata` (string map), `Overwrite` (default true).
- `StorageReadResult`: `Stream Content`, `string ContentType`, `long Length`, metadata.
- `StorageObjectInfo`: `string Key`, `long Length`, `string ContentType`, `string? ETag`.
- `PresignedUrlRequest`: `PresignedUrlOperation { Get, Put }`, `TimeSpan Expiry`, optional
  `ContentType` (for Put).

**Backends:**
- `LocalStorageProvider(LocalStorageOptions)` — filesystem under a configured root; keys map to a path
  (sanitized, no traversal). Presigned URLs are an **HMAC-signed token + expiry** that the module's
  download endpoint verifies and honors (the core has no HTTP server, so it returns a relative signed
  URI the module materializes).
- `S3StorageProvider(S3StorageOptions)` (in `Themia.Storage.S3`) — `IAmazonS3`; native `GetPreSignedURL`
  for Get/Put. `S3StorageOptions`: `BucketName`, `Region`, credentials (explicit keys **or** the
  default AWS credential chain), `ServiceUrl` (set for R2/MinIO/custom), `ForcePathStyle` (R2/MinIO
  require path-style). **R2** = `ServiceUrl = https://<account>.r2.cloudflarestorage.com` +
  `ForcePathStyle = true`; no R2-specific code.

Streaming `PutObject` for uploads; large uploads are handled client-side via **presigned PUT** (a
client uploads directly to S3/R2), so server-side multipart/TransferUtility is out of this slice.

## 5. Module — `ITenantStorage` (tenant-aware, peer-neutral)

The app-facing service. Same surface as `IStorageProvider` but on **logical keys**, with isolation,
metadata, and quota added:

```csharp
public interface ITenantStorage
{
    Task<StoredObject> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken ct = default);
    Task<StorageReadResult?> GetAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
    Task<Uri> GetDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken ct = default);
    Task<Uri> GetUploadUrlAsync(string key, PresignedUploadRequest request, CancellationToken ct = default);
}
```

### 5.1 Isolation by construction
Every logical key is mapped to a **physical key** `{tenantId}/{key}` before reaching the provider;
platform (tenant_id `null`) maps to `_platform/{key}`. Callers only ever pass logical keys and
**cannot** reference another tenant's blob — the prefix is applied centrally in one place
(`StorageScope.PhysicalKey`), the storage analogue of Identity's `IdentityScope`. Key inputs are
validated/sanitized (no `..`, no leading `/`, normalized separators) so a crafted key cannot escape
the prefix.

### 5.2 Metadata + quota (`storage_objects`)
A tenant-scoped table (FM-owned; see §6) records each object. Storage spans two systems (the DB and
the blob backend) with no shared transaction, so `PutAsync` is **metadata-first** for race-safe quota:

1. validate — size cap + content-type allowlist via `IFileValidator`.
2. scan — `IFileScanner` (no-op by default; server-proxied path only — presigned uploads scan at the
   complete step / async, a future concern while scanning is a no-op).
3. **reserve, inside one UoW transaction** (`ExecuteInTransactionAsync`): compute current tenant usage
   (`SUM(size_bytes)` over the tenant's rows, excluding the key being replaced), reject if
   `usage + newSize > quota`, then upsert the `storage_objects` row with the new size and commit. This is
   **transactional but best-effort** under high concurrency — quota-by-sum has no unique index to
   compare-and-set against, so two simultaneous puts could each read the pre-commit sum and overshoot
   slightly. Strict serialization (a per-tenant usage row updated with a conditional `UPDATE`, or a
   `SERIALIZABLE` transaction) is a deferred hardening (§12), not this slice.
4. write the blob via `IStorageProvider`.

If the blob write fails after the row commits, the reserved row is best-effort deleted in a
compensating step. The DB is the source of truth; the residue of a crash between commit and blob write
is a metadata row with no blob, swept by a future reconcile job (noted, not built). Quota +
max-object-size come from `StorageQuotaOptions` (a global default), with an optional per-tenant
override row in a future slice.

### 5.3 Seams (no-op defaults now, real impls later)
- `IFileValidator` — default enforces `MaxObjectSizeBytes` + a content-type allowlist; magic-byte
  sniffing is an opt-in validator (security rule: validate by content, not extension) wired in a
  later slice.
- `IFileScanner` — default `NullFileScanner` (pass-through); ClamAV (`Themia.Storage.ClamAV`, donor:
  PowerACC) is a deferred slice.

### 5.4 Peer neutrality
The metadata store uses `IRepository<StorageObject>` / `ISpecification` / `IUnitOfWork` /
`IDataFilterScope`, so the module runs unchanged on **EF Core OR Dapper** over one FM schema
(DECISION #6 parity), exactly like Identity.

## 6. Schema — `storage.storage_objects`

FM-owned (`Themia.Data.Migrations`), one migration with `IfDatabase("postgresql","sqlserver")`, the
single authority for both data layers (no `dotnet ef migrations add`). A dedicated `storage` schema
(bracketed `[storage]` on SQL Server).

`storage_objects` (`ITenantEntity`, soft-deletable, audited — mirrors Identity entities):

| column | notes |
|---|---|
| `id` (PK, Guid v7) | |
| `tenant_id` (nullable) | platform rows are `NULL` |
| `key` | the logical key (unprefixed) |
| `content_type` | |
| `size_bytes` | |
| `etag` (nullable) | provider ETag when available |
| `created_at` / `created_by` / `modified_*` / `is_deleted` | framework audit + soft-delete |

Filtered-unique indexes per the framework convention: `(tenant_id, key) WHERE tenant_id IS NOT NULL`
and `(key) WHERE tenant_id IS NULL` (per-tenant + platform uniqueness). Index `(tenant_id)` for the
quota `SUM` scan.

## 7. DI & configuration

A fluent builder mirroring `AddThemiaExternalAuth`:

```csharp
services.AddThemiaStorage(o => { o.MaxObjectSizeBytes = ...; o.DefaultTenantQuotaBytes = ...; })
        .UseLocal(o => o.RootPath = "/var/themia/blobs")   // or
        .UseS3(o => { o.BucketName = ...; o.Region = ...; /* creds */ })   // or
        .UseR2(o => { o.AccountId = ...; o.BucketName = ...; o.AccessKey = ...; o.SecretKey = ...; });
```

`UseR2` is sugar over `UseS3` (sets `ServiceUrl` + `ForcePathStyle = true`). Exactly one backend per
process (fail-fast if none/multiple, like the Dapper single-engine guard). Options are typed +
validated on startup (`ValidateOnStart`). `IThemiaModule` registers the service, the repository
binding, and the FM migration assembly.

## 8. Endpoints (opt-in, thin)

`MapThemiaStorageEndpoints()` (minimal API, opt-in like `MapIdentityAuthEndpoints`). Default flow is
**presigned direct transfer** (client ↔ S3/R2, server only brokers):

- `POST /storage/upload-url` → reserve a **pending** `storage_objects` row (quota-counted at the
  declared size, but **invisible** to `Get`/`Exists` until completed) + return a **presigned PUT** URL.
- *(client uploads the bytes directly to the presigned URL)*
- `POST /storage/complete` → confirm the upload: `Stat` the actually-stored bytes, validate + re-check
  the per-tenant quota against the **actual** size (not the declared size), then commit the reservation
  (record actual `size_bytes`/`etag`, mark `committed_at` → visible). A quota overrun discards the
  orphaned blob + reservation and returns the quota error.
- `GET /storage/{id}` → **presigned GET** redirect (Local: signed download route).
- `DELETE /storage/{id}` → delete blob + soft-delete row.

The reserve→upload→complete flow closes two quota/visibility gaps: a presigned reservation can no
longer leave a phantom (uploaded-but-never-confirmed) object visible, and quota is reconciled to the
**actual** stored size at completion so an under-declared size cannot bypass the quota.

A server-proxied `PUT` (multipart form → stream through the service) is supported for small files.
Endpoints are thin; clean-arch consumers (ezy-assets) may call `ITenantStorage` from their own
controllers instead.

## 9. Security

- **Tenant isolation by construction** (§5.1): central prefixing + key sanitization; no path traversal.
- **No secrets logged.** Credentials/presigned URLs/tokens are never written to logs (`ILogger<T>`
  only). Presigned URLs have short expiries.
- **Content validation** by size + content-type allowlist now; magic-byte sniffing as a validator
  option (deferred). **Scan seam** in the upload pipeline (ClamAV deferred).
- **Local backend** stores outside the web root; the signed download route enforces tenant scope +
  expiry. Original filenames are never used as physical keys.
- A future `Themia.Analyzers` rule (THEMIA10x) flagging raw `IStorageProvider` use outside the module
  (bypassing the tenant prefix) is a noted follow-up, not in this slice.

## 10. Testing

- **Provider conformance** against **Testcontainers MinIO** (S3-compatible — exercises both the S3 and
  R2 path-style configurations with no AWS account) + the Local backend: put/get/exists/delete,
  overwrite, presigned Get/Put round-trips, absent-key behavior.
- **Metadata + quota** on the **4-way matrix** (EF×Dapper × PostgreSQL×SQL Server, Testcontainers),
  like Identity: quota enforced at threshold, quota race-safe under concurrent puts (transactional
  sum), tenant isolation (a tenant cannot read/list/delete another tenant's object; platform vs
  tenant), soft-delete hides the row.
- **Unit:** key-prefixing + sanitization (traversal rejected), quota math, `IFileValidator` allow/deny,
  presigned-token signing/verification + expiry, single-backend fail-fast.

## 11. Out of scope (deferred)

- **ClamAV scanner** implementation (`IFileScanner` seam ships; impl later).
- **Magic-byte content sniffing** validator (size + content-type allowlist now).
- **Server-side multipart / TransferUtility** (presigned PUT offloads large uploads to the client).
- **Per-tenant quota override rows** (global default now).
- **Azure Blob / GCS** providers (additive packages later).
- **Per-tenant bucket** isolation (key-prefixing chosen).
- **Orphan-blob reconcile** job and the raw-provider-bypass analyzer (noted follow-ups).

Each deferred item has a home in the backlog (§12).

## 12. Backlog (needs-driven, no committed version)

**0.5.3 (this spec) shipped the foundation.** The hardening below is **deferred until a consumer
actually needs it** — no fixed 0.5.4/0.5.5 slices. Each is enhancement to a Phase-1 module, so it stays
Phase-1 work (phase = module category, not version) and ships under whatever version it lands; it does
**not** gate Phase 2 (Notifications / Pdf / Export), which opens at **0.6.0**.

- **Content trust** — `Themia.Storage.ClamAV` (`IFileScanner` impl, donor: PowerACC), stream-scan on
  server-proxied upload + a post-upload scan hook for presigned uploads; magic-byte content-sniffing
  `IFileValidator`. *Pull when a consumer accepts untrusted uploads.*
- **Scale & ops** — DB-side quota `SUM` / a framework `SumAsync` aggregate (today the sum materializes
  rows); strict quota serialization (per-tenant usage row + conditional `UPDATE`, replacing the
  best-effort sum); per-tenant quota overrides; orphan-blob + abandoned-reservation reconcile job;
  server-side multipart / `TransferUtility`; a `Themia.Analyzers` THEMIA10x rule flagging raw
  `IStorageProvider` use outside the module. *Pull when a tenant gets large or throughput matters.*
- **More backends** — Azure Blob / GCS provider packages; optional per-tenant-bucket isolation. *Pull
  only if a consumer needs them.*
