# Themia.Modules.Pdf (Tenant-Aware Template Store + Render-by-Key) — Design

**Status:** Approved (brainstorming) — ready for implementation plan
**Date:** 2026-07-07
**Origin:** Sub-project #2 of the Pdf effort. Follows the neutral core
`docs/superpowers/specs/2026-06-21-themia-pdf-neutral-core-design.md`, which explicitly deferred
this module to its own cycle.
**Target version:** next Phase-2 `0.x.0` (new module package; see the release strategy
`docs/superpowers/specs/2026-06-01-themia-release-strategy-design.md`).

---

## Goal

Add the deferred tenant-aware **template store** on top of the already-shipped stateless renderer
(`Themia.Pdf`): persist HTML/Handlebars templates per tenant with a global default fallback, and
expose a one-call **render-by-key** service that composes the store with the neutral renderer
(resolve → Handlebars merge → PuppeteerSharp → PDF bytes).

The neutral core is stateless and dependency-free; this module supplies the state (schema, tenant
isolation, global fallback, CRUD) that the neutral spec intentionally left out.

## Scope

### In scope

- `PdfTemplate` entity + FluentMigrator schema (single migration, `IfDatabase(...)` per engine) as
  the single DDL authority for both data peers.
- `IPdfTemplateStore` — CRUD (`Create/Update/Delete/Get/List`) + `ResolveAsync(key)` with
  tenant-override → global-default fallback.
- **Both data peers** — an EF Core store and a Dapper store over the **same schema**, both enforcing
  tenant isolation + soft-delete + audit through the framework layers.
- `IPdfDocumentRenderer` — render-by-key convenience composing the store with the neutral
  `IHtmlTemplateRenderer` + `IPdfRenderer`.
- `IThemiaModule` implementation for module discovery.
- Per-peer DI entry points: `AddThemiaPdfModuleEfCore(...)` / `AddThemiaPdfModuleDapper(...)`.

### Out of scope (v1 — YAGNI)

- **Version history / publish workflow** (draft/published, immutable revisions). Resolution is
  latest-row, no versioning. Deferred; a versions table + publish flow is a later cycle if asked.
- **AspNetCore endpoints** (`Themia.Modules.Pdf.AspNetCore` CRUD/render API). Endpoints are often
  app-specific; add later only if a consumer needs them (mirrors the neutral spec's YAGNI stance on
  `Themia.Pdf.AspNetCore`).
- **Per-template stored render options** — paper size/margins are passed at the render call,
  defaulting to `PdfRenderOptions` defaults. Storing per-template defaults is a cheap future add.
- **Optimistic concurrency token** (`IConcurrencyAware`/`RowVersion`) — see Decision 3 below.
- `ProposalPdfService` and app-domain document mapping — stay in ezy (per the neutral spec).

### Scope-guard check

A tenant-scoped store of reusable HTML templates with a global fallback is cross-cutting
infrastructure (any multi-tenant app rendering documents needs it). The per-document field mapping
stays app-side (the render service takes an opaque `object model`). ✅

---

## Key decisions

### Decision 1 — EF + Dapper peers, all three engines (MySQL via Dapper only)

Both data peers are first-class (per the architecture overview's DECISION #6): an adopter picks one
for their app, and this module ships a store implementation for each over one schema.

**Engine × peer matrix (framework-wide reality, not a Pdf-specific gap):**

| Engine | EF store | Dapper store |
|---|---|---|
| SQL Server | ✅ | ✅ |
| PostgreSQL | ✅ | ✅ |
| MySQL / MariaDB | ❌ (no EF Core 10 provider) | ✅ |

There is **no EF Core 10 MySQL provider** anywhere in Themia (Pomelo has no EF-10 build — verified
2026-07-07, latest published `Pomelo.EntityFrameworkCore.MySql` is `9.0.0` targeting EF Core 9). So
MySQL is delivered through the **Dapper peer**; an adopter who needs MySQL runs on Dapper. The
FluentMigrator migration still emits DDL for all three engines (it is engine-agnostic).

### Decision 2 — Single package (Approach A)

One `Themia.Modules.Pdf` (net10.0) references both framework data peers plus the neutral
`Themia.Pdf`, and exposes two DI entry points. Matches the Notifications/Export precedent and keeps a
single FluentMigrator migration. The cost — an app that uses one peer still pulls both peers'
dependencies transitively — is minor and pre-1.0, and is exactly the tradeoff Notifications already
accepts. No module-specific engine sub-packages are needed: engine specifics (SQL compiler,
connection factory, exception interpreter) come from the framework's existing
`Themia.Framework.Data.EFCore.{SqlServer,PostgreSql}` and
`Themia.Framework.Data.Dapper.{SqlServer,PostgreSql,MySql}` packages the app already registers.
(Notifications shipped its own `.SqlServer/.PostgreSql/.MySql` packages only because it has bespoke
outbox SQL — SKIP-LOCKED atomic claim — which a plain CRUD store does not.)

### Decision 3 — No optimistic concurrency token in v1

`PdfTemplate` does **not** implement `IConcurrencyAware`. Template CRUD is low-contention admin data,
and the framework's `byte[] RowVersion` mapping is a documented **landmine on MySQL**: it maps to a
server-maintained `rowversion` on SQL Server and to `xmin` on Postgres, but **MySQL/MariaDB have no
`rowversion` concept** and silently fall through the SQL-Server branch, so the token is never
server-updated and concurrency never fires (see the `LANDMINE for future providers` remark in
`ThemiaDbContext.ApplyConcurrencyTokens`). Adding the token would buy no real protection on the one
engine the Dapper peer exists to serve, while adding cross-engine risk. Skipping it keeps all three
engines correct by construction. Reversible later (add the marker + a per-engine token mapping) if a
real contention case appears.

### Decision 4 — Global rows use the framework's built-in support (not a new mechanism)

The framework already models "global/shared" rows as `TenantId == null` and includes them in
tenant-scoped reads on **both** peers:

- EF: `ThemiaDbContext.IncludeGlobalRecordsForTenants => true` and `BuildTenantPredicate` yield
  `current-tenant rows OR global (null) rows`; `ValidateTenantAccess` mirrors it for `Find`.
- Dapper: `TenantPredicate.Apply` emits `WHERE tenant_id = @t OR tenant_id IS NULL` when
  `includeGlobalRecords` is set (SqlKata query, rendered per-engine by the framework SQL compiler).

So "tenant-override → global fallback" is native, not invented here.

---

## Architecture

**One package:** `src/modules/Themia.Modules.Pdf`, TFM `net10.0` (module convention).

**Project references:**

- `Themia.Framework.Core` (entities/tenancy abstractions).
- `Themia.Framework.Data.Abstractions` (filtering, UoW abstractions).
- `Themia.Framework.Data.EFCore` (EF store base `ThemiaDbContext`).
- `Themia.Framework.Data.Dapper` (`DapperRepository`, `ITenantQueryFactory`, `ISqlCompiler`,
  `EntityMapping`).
- `Themia.Data.Migrations` (FluentMigrator conventions).
- `Themia.Pdf` (neutral renderer — `IHtmlTemplateRenderer`, `IPdfRenderer`, `PdfRenderOptions`).

**Package references:** `Microsoft.EntityFrameworkCore.Relational`, `FluentMigrator`, `Dapper`,
`Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`,
`Microsoft.CodeAnalysis.PublicApiAnalyzers` (mirroring the Export module's set, minus the
engine-specific EF provider packages, which stay app-side).

---

## Components

### 1. `PdfTemplate` (entity)

```csharp
public sealed class PdfTemplate : SoftDeletableEntity<Guid>, ITenantEntity, IAuditableEntity
{
    /// <summary>Owning tenant; null for a global default template.</summary>
    public TenantId? TenantId { get; set; }

    /// <summary>Resolution key (e.g. "invoice"). Unique per tenant, and once globally.</summary>
    public required string Key { get; set; }

    /// <summary>The Handlebars/HTML template source rendered against a model.</summary>
    public required string Body { get; set; }

    /// <summary>Human-readable label for management UIs.</summary>
    public string? Name { get; set; }

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }
}
```

- Base classes supply `Id` + audit + soft-delete columns, mapped to the framework's fixed snake_case
  names (`id`, `tenant_id`, `created_at`, `created_by`, `last_modified_at`, `last_modified_by`,
  `is_deleted`, `deleted_at`, `deleted_by`, `restored_at`, `restored_by`) so the EF and Dapper peers
  agree on one schema.
- Adopter columns (`key`, `body`, `name`, `description`) map to snake_case (`key`, `body`, `name`,
  `description`) — EF via entity configuration, Dapper via `EntityMapping.ToSnakeCase`, so both peers
  and the single migration line up.

### 2. Schema (FluentMigrator, single migration)

- One migration in `Migrations/`, `IfDatabase("sqlserver"/"postgres"/"mysql")` branches, as the sole
  DDL authority for both peers (no `dotnet ef migrations add`).
- Table `pdf_templates` with the columns above.
- **Uniqueness — "one template per key per tenant, one global per key":** a unique index on
  `(tenant_id, key)`. NULL semantics differ per engine, so the migration handles the global-row case
  explicitly per `IfDatabase`:
  - **SQL Server** — a filtered unique index `WHERE tenant_id IS NOT NULL` for the tenant rows, plus
    a second filtered unique index on `(key) WHERE tenant_id IS NULL` for the single global per key.
  - **PostgreSQL** — `UNIQUE NULLS NOT DISTINCT (tenant_id, key)` (PG 15+) so a NULL tenant collides
    on `key` as desired; fallback to two partial indexes if the target PG version predates it.
  - **MySQL/MariaDB** — MySQL treats NULLs as distinct in unique indexes, so a single unique
    `(tenant_id, key)` does **not** enforce one-global-per-key; add the same two-index split (a
    functional/partial-equivalent) or a generated-column guard. Exact mechanism finalized in the
    plan.
- Index on `key` for resolution lookups (covered by the uniqueness indexes above).

### 3. `IPdfTemplateStore`

```csharp
public interface IPdfTemplateStore
{
    Task<PdfTemplate> CreateAsync(PdfTemplate template, CancellationToken ct = default);
    Task<PdfTemplate> UpdateAsync(PdfTemplate template, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<PdfTemplate?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken ct = default);

    /// <summary>Resolves a template by key for the ambient tenant: the tenant's own row if present,
    /// else the global (null-tenant) default, else throws <see cref="TemplateNotFoundException"/>.</summary>
    Task<PdfTemplate> ResolveAsync(string key, CancellationToken ct = default);
}
```

Two implementations over the same schema:

- **`EfPdfTemplateStore`** — a `PdfDbContext : ThemiaDbContext` with a `DbSet<PdfTemplate>` and entity
  configuration. Tenant + soft-delete + global-inclusion filters come from the base context; audit
  fields from `SaveChanges`.
- **`DapperPdfTemplateStore`** — `DapperRepository`/`ITenantQueryFactory.For<PdfTemplate>()` with the
  tenant + global predicate pre-seeded; audit fields set through the Dapper layer's auditing hook.

Both are `internal` (consumers depend on `IPdfTemplateStore` via DI).

**Resolution logic (identical intent on both peers):** query `WHERE key = @key` (framework filter
auto-adds `tenant = @t OR tenant IS NULL` and `is_deleted = false`), yielding at most one tenant row
and one global row; prefer the tenant-owned row, else the global, else throw `TemplateNotFoundException`.

**Write asymmetry (framework-enforced; documented as an API fact):** the framework blocks a tenant
context from writing global (null-tenant) rows — only a **no-tenant (system) scope** may create or
edit global defaults (`ThemiaDbContext.ValidateTenantWritesAsync`; the Dapper layer's
`WHERE tenant_id = …` equivalent). The module documents this and does not add its own authorization
(authz stays the app's concern, consistent with sibling modules).

### 4. `IPdfDocumentRenderer` (render-by-key)

```csharp
public interface IPdfDocumentRenderer
{
    /// <summary>Resolves the template for <paramref name="key"/> (tenant → global), merges it with
    /// <paramref name="model"/>, and prints the result to a PDF.</summary>
    Task<byte[]> RenderAsync(
        string key,
        object model,
        PdfRenderOptions? options = null,
        CancellationToken ct = default);
}
```

Composition only: `store.ResolveAsync(key)` → `IHtmlTemplateRenderer.Render(tpl.Body, model)` →
`IPdfRenderer.RenderHtmlAsync(html, options, ct)` → bytes. No new rendering logic. `null` options ⇒
neutral-core defaults (A4 / background / 20-20-15-15 margins).

### 5. DI + module

```csharp
public static IServiceCollection AddThemiaPdfModuleEfCore(
    this IServiceCollection services, Action<PdfModuleOptions>? configure = null);

public static IServiceCollection AddThemiaPdfModuleDapper(
    this IServiceCollection services, Action<PdfModuleOptions>? configure = null);
```

- The app calls the entry point matching its chosen data peer.
- Each registers the matching `IPdfTemplateStore`, `IPdfDocumentRenderer`, and calls `AddThemiaPdf()`
  (the neutral renderer) if not already present. Idempotent via `TryAdd*`.
- `PdfModule : IThemiaModule` for discovery/migration wiring (mirror `ExportModule`).
- `PdfModuleOptions` carries module-level config (kept minimal for v1; passes a `ConfigureHandlebars`
  hook through to the neutral options if needed).

---

## Error handling & logging

- `TemplateNotFoundException` when resolve finds neither a tenant nor a global template — HTTP-agnostic
  (no `StatusCode`; the ASP.NET Core middleware in `Themia.AspNetCore` owns any HTTP mapping).
- Render failures propagate from the neutral core unchanged.
- `ILogger<T>` only — no `Console.*`. Log a resolve miss at `Warning`, store/render failures at
  `Error`, once per failure (no double-logging).
- `OperationCanceledException` propagates as cancellation — never caught-and-swallowed.

---

## Public API surface (PublicAPI analyzer)

New public members (added to `PublicAPI.Unshipped.txt`, XML-documented):

- `Themia.Modules.Pdf.PdfTemplate` (+ properties)
- `Themia.Modules.Pdf.IPdfTemplateStore` (+ methods)
- `Themia.Modules.Pdf.IPdfDocumentRenderer` (+ `RenderAsync`)
- `Themia.Modules.Pdf.TemplateNotFoundException`
- `Themia.Modules.Pdf.PdfModule`
- `Themia.Modules.Pdf.PdfModuleOptions`
- `Microsoft.Extensions.DependencyInjection.ThemiaPdfModuleServiceCollectionExtensions`
  (`AddThemiaPdfModuleEfCore` / `AddThemiaPdfModuleDapper`)

Concrete stores (`EfPdfTemplateStore`, `DapperPdfTemplateStore`), the `PdfDbContext`, and the entity
configuration are `internal`.

---

## Testing strategy

**Unit (no DB, no Chromium):**

- Resolve precedence: tenant-owned row wins over global; global used when no tenant row; both absent
  ⇒ `TemplateNotFoundException`.
- `IPdfDocumentRenderer.RenderAsync` composes resolve → merge → render (fakes for the neutral
  interfaces; assert the resolved body is what gets merged, and options flow through).
- DI: each entry point registers the store + renderer + neutral core; idempotent; the correct store
  impl is bound per entry point.

**Integration (Testcontainers — real engines, per `dotnet.md`):**

- FluentMigrator schema applies cleanly on **SQL Server, PostgreSQL, and MySQL**; the uniqueness
  rules enforce one-per-tenant and one-global-per-key on each engine (insert-conflict assertions).
- EF store CRUD + resolve on **SQL Server + PostgreSQL**.
- Dapper store CRUD + resolve on **all three engines** (this is the peer that exercises MySQL).
- Write asymmetry: a tenant-scoped context cannot create/edit a global row; a system (no-tenant)
  scope can.
- Cross-peer schema parity: a row written by the EF store is readable by the Dapper store and vice
  versa (SQL Server / Postgres).

**Integration (Chromium — gated like the neutral suite):**

- End-to-end `RenderAsync(key, model)` resolves a stored template and returns bytes beginning with
  the `%PDF-` magic header. Requires a Chromium provision step; surface the requirement in the test
  project README / CI, do not silently skip.

---

## Versioning, changelog, coordination

- Bump `Directory.Build.props` `<Version>` to the next Phase-2 `0.x.0` (new module package).
- CHANGELOG **Added — `Themia.Modules.Pdf`**: tenant-aware HTML/PDF template store (EF + Dapper peers,
  SQL Server / PostgreSQL / MySQL-on-Dapper) with global-default fallback and a render-by-key service
  over the neutral `Themia.Pdf` core.
- Complete the neutral-core coord request (ezy → Themia.Pdf) once the store lands, if the origin ask
  covered template management.

---

## Future improvements (not v1)

- Version history + publish workflow (draft/published, pinned revisions).
- `Themia.Modules.Pdf.AspNetCore` — CRUD + render endpoints, only if a consumer needs them.
- Per-template default render options (paper/margins) stored on the row.
- Optimistic concurrency token with a correct per-engine mapping (incl. MySQL), if contention appears.
- Bounded compiled-template cache — already tracked as a neutral-core future win.
