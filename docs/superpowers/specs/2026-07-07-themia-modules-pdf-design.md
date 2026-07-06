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
- **Framework prerequisite (small, sequenced first):** a per-query global-inclusion override on the
  sanctioned Dapper path — `ITenantQueryFactory.For<T>(bool includeGlobalRecords)` — so the Dapper
  resolve can include global rows for `PdfTemplate` without depending on the app-wide
  `DapperDataOptions.IncludeGlobalRecordsForTenants` default. See Decision 4.
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

### Decision 4 — Global-row inclusion: native on EF, one small framework addition on Dapper

The framework models "global/shared" rows as `TenantId == null`, but the two peers default
differently, and the difference is load-bearing:

- **EF — works out of the box.** `ThemiaDbContext.IncludeGlobalRecordsForTenants => true` (default) and
  `BuildTenantPredicate` yield `current-tenant rows OR global (null) rows`; `ValidateTenantAccess`
  mirrors it for `Find`. The flag is **per-context**, and `PdfDbContext` is dedicated to
  `PdfTemplate`, so the `true` default is safe — it never affects unrelated entities. No change
  needed.
- **Dapper — off by default, and the app switch is the wrong lever.**
  `DapperDataOptions.IncludeGlobalRecordsForTenants` defaults to **`false`**, and
  `TenantQueryFactory.For<T>()` reads that **app-wide** option. Under default config the Dapper
  resolve would emit `WHERE tenant_id = @t` with no `OR tenant_id IS NULL`, so the global fallback
  silently returns not-found. The module **cannot** fix this by flipping the option: it is a single
  switch shared across every tenant entity on the Dapper layer, so setting it `true` to satisfy
  `PdfTemplate` would change global-record semantics for all of the app's other Dapper entities.

**Resolution (framework prerequisite):** add a per-query override to the sanctioned Dapper path so a
caller can opt a single query into global inclusion without touching the app-wide default:

```csharp
public interface ITenantQueryFactory
{
    Query For<T>();
    Query For<T>(bool includeGlobalRecords);   // NEW — overrides DapperDataOptions for this query
}
```

The plumbing already exists — `TenantPredicate.Apply(query, tenant, includeGlobalRecords, ...)` takes
the flag as a parameter; only `For<T>()` hard-wires it from `options.IncludeGlobalRecordsForTenants`.
The new overload passes the caller's value instead. The Dapper resolve calls
`For<PdfTemplate>(includeGlobalRecords: true)`. This keeps resolution on the **sanctioned** query path
(no hand-rolled raw SQL, so the Dapper analyzer gate stays satisfied) and confines the behavior change
to the one query that asks for it.

**Sequencing / coordination:** this framework change lands in `Themia.Framework.Data.Dapper`
**before** the module store consumes it (its own commit, with a PublicAPI entry + a unit test that
`For<T>(true)` emits the `OR tenant_id IS NULL` clause and `For<T>(false)` does not). The
implementation plan orders it as task 0.

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
- **FluentMigrator caveat:** filtered/partial unique indexes and `NULLS NOT DISTINCT` are not
  expressible through FluentMigrator's fluent index API — each `IfDatabase(...)` branch uses
  `Execute.Sql(...)` raw DDL for the uniqueness indexes. Confirm the target PostgreSQL is 15+ for
  `NULLS NOT DISTINCT`; otherwise use the two-partial-index form on all three engines (it works
  everywhere and avoids the version dependency).
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

Two implementations over the same schema. **Both write through the framework Unit of Work**
(`IUnitOfWork` / `EfUnitOfWork` / `DapperUnitOfWork`), never a bare `DbContext.SaveChanges` — the
tenant-write guards fire **only** on the UoW path (see Write asymmetry below), so a direct save would
silently bypass them.

- **`EfPdfTemplateStore`** — a `PdfDbContext : ThemiaDbContext` with a `DbSet<PdfTemplate>` and entity
  configuration. Tenant + soft-delete + global-inclusion filters come from the base context (its
  `IncludeGlobalRecordsForTenants` default `true` is safe — the context holds only `PdfTemplate`).
  Writes flushed via `EfUnitOfWork` so `ValidateTenantWritesAsync` runs; audit fields stamped by the
  context on save.
- **`DapperPdfTemplateStore`** — reads via `ITenantQueryFactory.For<PdfTemplate>(includeGlobalRecords:
  true)` (the Decision-4 override, so the global fallback is included regardless of the app-wide
  option); writes via `DapperRepository` + `DapperUnitOfWork` (insert stamps `tenant_id` from the
  ambient tenant; update/delete are tenant-scoped by primary key).

Both are `internal` (consumers depend on `IPdfTemplateStore` via DI).

**Resolution logic (identical intent on both peers):** query `WHERE key = @key` (framework predicate
auto-adds `tenant = @t OR tenant IS NULL` and `is_deleted = false`), yielding at most one tenant row
and one global row; prefer the tenant-owned row, else the global, else throw `TemplateNotFoundException`.

**Write asymmetry (framework-enforced; documented as an API fact):** the framework blocks a tenant
context from writing global (null-tenant) rows — only a **no-tenant (system) scope** may create or
edit global defaults. On EF this is `ThemiaDbContext.ValidateTenantWritesAsync`, invoked by
`EfUnitOfWork`; on Dapper it is `DapperUnitOfWork` — insert stamps `tenant_id` from the ambient tenant
(so a tenant context creating a `TenantId = null` template gets a tenant-owned row, not a global one),
and update/delete are scoped to the ambient tenant's rows (a no-tenant scope is scoped to
`tenant_id IS NULL`). Both guarantees hold **only** because writes go through the UoW. The module
documents this and does not add its own authorization (authz stays the app's concern, consistent with
sibling modules).

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

**Lifetime — must be `scoped` (not singleton).** It depends on the **tenant-scoped**
`IPdfTemplateStore` (which reads the ambient tenant) while the neutral `IHtmlTemplateRenderer` /
`IPdfRenderer` it also uses are **singletons** (they own the long-lived browser). Registering
`IPdfDocumentRenderer` as a singleton would capture the scoped store — a captive dependency that
freezes the first request's tenant into every later resolve (or fails DI scope validation at startup).
Register it `scoped`; the singleton renderers inject into a scoped consumer safely.

### 5. DI + module

```csharp
public static IServiceCollection AddThemiaPdfModuleEfCore(
    this IServiceCollection services, Action<PdfModuleOptions>? configure = null);

public static IServiceCollection AddThemiaPdfModuleDapper(
    this IServiceCollection services, Action<PdfModuleOptions>? configure = null);
```

- The app calls the entry point matching its chosen data peer.
- Each registers the matching `IPdfTemplateStore` (**scoped**), `IPdfDocumentRenderer` (**scoped** —
  see Component 4), and calls `AddThemiaPdf()` (the neutral renderer — **singletons**) if not already
  present. Idempotent via `TryAdd*`.
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

**Framework prerequisite (unit, in `Themia.Framework.Data.Dapper` tests):**

- `ITenantQueryFactory.For<T>(includeGlobalRecords: true)` compiles a query whose SQL contains
  `tenant_id = @t OR tenant_id IS NULL`; `For<T>(false)` (and the app-wide default) does not. Lands
  with the framework change, before the module store.

**Unit (no DB, no Chromium):**

- Resolve precedence: tenant-owned row wins over global; global used when no tenant row; both absent
  ⇒ `TemplateNotFoundException`.
- `IPdfDocumentRenderer.RenderAsync` composes resolve → merge → render (fakes for the neutral
  interfaces; assert the resolved body is what gets merged, and options flow through).
- **Lifetime/tenant-isolation:** resolving through a scoped `IPdfDocumentRenderer` under tenant A then
  tenant B returns each tenant's own template — guards against the captive-dependency regression
  (Component 4). Assert DI scope validation passes (no scoped-into-singleton capture).
- DI: each entry point registers the store + renderer (both **scoped**) + neutral core (**singleton**);
  idempotent; the correct store impl is bound per entry point.

**Integration (Testcontainers — real engines, per `dotnet.md`):**

- FluentMigrator schema applies cleanly on **SQL Server, PostgreSQL, and MySQL**; the uniqueness
  rules enforce one-per-tenant and one-global-per-key on each engine (insert-conflict assertions).
- EF store CRUD + resolve on **SQL Server + PostgreSQL**.
- Dapper store CRUD + resolve on **all three engines** (this is the peer that exercises MySQL).
- **Dapper global fallback specifically:** with the app-wide `DapperDataOptions` default (globals
  off), a tenant with no own template still resolves the global default — proving the
  `For<PdfTemplate>(includeGlobalRecords: true)` override works independently of the app option (the
  finding-#1 regression test).
- Write asymmetry: a tenant-scoped context cannot create/edit a global row (a `TenantId = null`
  create under a tenant becomes tenant-owned); a system (no-tenant) scope creates/edits globals.
  Verified on **both peers** through the framework UoW.
- Cross-peer schema parity: a row written by the EF store is readable by the Dapper store and vice
  versa (SQL Server / Postgres).

**Integration (Chromium — gated like the neutral suite):**

- End-to-end `RenderAsync(key, model)` resolves a stored template and returns bytes beginning with
  the `%PDF-` magic header. Requires a Chromium provision step; surface the requirement in the test
  project README / CI, do not silently skip.

---

## Versioning, changelog, coordination

- Bump `Directory.Build.props` `<Version>` to the next Phase-2 `0.x.0` (new module package).
- CHANGELOG **Added — `Themia.Framework.Data.Dapper`**: `ITenantQueryFactory.For<T>(bool
  includeGlobalRecords)` overload (additive) — a per-query global-inclusion override. Ships first (the
  Decision-4 prerequisite).
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
