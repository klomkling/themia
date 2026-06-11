# Themia 0.4.5 — EF Core SQL Server provider (design)

> Spec. Master context: [`themia-architecture-overview.md`](../../themia-architecture-overview.md)
> (esp. **DECISION #6 — Data-access peers & schema authority**). Status date: 2026-06-11.

## Goal

Add a SQL Server EF Core provider as a **first-class peer** to the existing Postgres provider —
restructuring the EF layer into per-engine packages (mirroring the Dapper layer) and scoping the
framework's column-naming so adopters get **idiomatic PascalCase on SQL Server** without breaking
EF↔Dapper schema parity.

This is the EF mirror of the 0.4.4 Dapper SQL Server engine and the first step in closing the
EF-is-PostgreSQL-only gap that motivated DECISION #6 (EF and Dapper are selectable first-class peers).

## Scope

**In:**
- Per-engine package split: core EFCore becomes provider-agnostic; Postgres extracted to its own
  package; new SQL Server package.
- `SqlServerDatabaseProvider` + `AddThemiaSqlServer` registration.
- Scoped framework-column naming: framework columns explicitly mapped to snake_case in
  `ThemiaDbContext`; the global snake_case convention becomes an opt-in flag (default off).
- SQL Server integration tests (Testcontainers).

**Out (later releases, per DECISION #6 follow-ups):**
- EF MySQL provider → **0.4.6**.
- FluentMigrator schema standardization (Scheduling rewrite, aggregating runner, per-provider
  concurrency-token DDL) → **0.4.7**.
- Raw-connection analyzer guard (the Dapper-as-peer gate) → **0.4.8**.

## Decisions (resolved during brainstorming, 2026-06-11)

1. **Package topology — per-engine, extract Postgres too.** Mirror the Dapper layer's per-engine
   package split rather than baking providers into core. Chosen over "all providers in core" (avoids
   dragging every provider's EF package onto every consumer) and over "split new provider only, defer
   PG extraction" (which leaves core still dragging Npgsql — an incoherent interim).
2. **Column naming — framework columns snake_case everywhere; adopter columns are the adopter's
   choice.** Themia owns the naming of *its own* (framework base-entity + module) columns and keeps
   them snake_case across all engines (parity with Dapper's hardcoded snake_case convention and one
   FluentMigrator schema). It does **not** dictate the adopter's app-table naming — on SQL Server the
   adopter's own columns default to PascalCase. Chosen over "PascalCase for the whole EF SqlServer
   model" (would force snake_case Dapper and PascalCase EF to need different schemas on the same
   engine, breaking DECISION #6 peer parity) and over "change Dapper to PascalCase too" (a large,
   breaking, cross-cutting change to the shipped 0.4.4 Dapper SqlServer engine + Dapper core +
   FluentMigrator, well beyond this release).

## Architecture

### 1. Package topology

`Themia.Framework.Data.EFCore` (core) becomes **provider-agnostic**. It keeps the engine-neutral
pieces and drops all provider-specific dependencies:

```
Themia.Framework.Data.EFCore                 (core — provider-agnostic, net10.0)
  keeps:  ThemiaDbContext, IDatabaseProvider, DatabaseProviderNames,
          Repositories (EfRepository, EfReadRepository), UnitOfWork (EfUnitOfWork),
          Infrastructure (TenantModelCacheKeyFactory), Abstractions, ServiceCollectionExtensions
          (the provider-agnostic AddThemiaDbContext overloads)
  drops:  Npgsql.EntityFrameworkCore.PostgreSQL, EFCore.NamingConventions,
          PostgresDatabaseProvider, AddThemiaPostgres, CreateProvider's postgres branch

Themia.Framework.Data.EFCore.PostgreSql      (new package, net10.0)
  PostgresDatabaseProvider + AddThemiaPostgres
  deps: Themia.Framework.Data.EFCore + Npgsql.EntityFrameworkCore.PostgreSQL

Themia.Framework.Data.EFCore.SqlServer       (new package, net10.0)
  SqlServerDatabaseProvider + AddThemiaSqlServer
  deps: Themia.Framework.Data.EFCore + Microsoft.EntityFrameworkCore.SqlServer
        (no EFCore.NamingConventions — superseded; see the §3 amendment: whole-model snake_case is the
        adopter's own configureOptions + package reference)
```

This mirrors `Themia.Framework.Data.Dapper(.PostgreSql/.SqlServer)`. A consumer references only the
provider package it uses and transitively gets the core.

**Breaking change (pre-1.0, acceptable):** existing `AddThemiaPostgres` callers must add a
`Themia.Framework.Data.EFCore.PostgreSql` package reference and a `using` for the new namespace.
There are no external adopters; doing the move now is cheaper than later. Recorded in `MIGRATION.md`.

**`CreateProvider` / `AddThemiaDbContextWithProvider`:** the name→provider switch currently lives in
core and hardcodes the Postgres type. After the split, core cannot reference provider types. Resolve
by **moving the name-based factory out of core** — each provider package exposes its own
`AddThemiaSqlServer` / `AddThemiaPostgres` entry point (the primary, type-safe API), and the
string-name `AddThemiaDbContextWithProvider` is dropped from core (it was a convenience wrapper with
one supported value). If a name-based selector is still wanted later it can return to a composition
package; YAGNI for now.

### 2. `SqlServerDatabaseProvider`

A direct mirror of `PostgresDatabaseProvider`:

- `ProviderName => DatabaseProviderNames.SqlServer` (`"sqlserver"` — constant already exists).
- `Configure(optionsBuilder, configuration, serviceProvider)`:
  - `var connectionString = ResolveConnectionString(configuration, serviceProvider);`
  - `optionsBuilder.UseSqlServer(connectionString, ConfigureSqlServerOptions);`
  - **No `.UseSnakeCaseNamingConvention()`** (see §3).
- `ResolveConnectionString` — identical semantics to the Postgres provider: prefer
  `serviceProvider.GetService<ITenantAccessor>()?.Current?.ConnectionString` (DB-per-tenant), else the
  `Default` connection string; throw `InvalidOperationException` when neither exists. Extract the
  shared resolution into a small internal helper reused by both providers (it is identical today and
  must not drift) rather than copy-pasting.
- `ConfigureSqlServerOptions(SqlServerDbContextOptionsBuilder)` — **does not** call
  `EnableRetryOnFailure`, for the same reason the Postgres provider omits it: a retrying execution
  strategy is incompatible with the user-initiated transactions exposed by
  `IUnitOfWork.BeginTransactionAsync`. Documented identically. Hosts that need transient-fault
  resilience and do not use manual transactions can opt in via the `configureOptions` delegate.
- `ConfigureServices` — no eager connection-string validation (same rationale as Postgres:
  DB-per-tenant supplies the connection string per scope).

`AddThemiaSqlServer<TContext>(this IServiceCollection, IConfiguration, Action<DbContextOptionsBuilder>?
= null)` mirrors `AddThemiaPostgres`: constructs the provider and delegates to the core
`AddThemiaDbContext<TContext>(provider, configuration, configureOptions)`.

### 3. Scoped framework-column naming

**Problem.** Today `PostgresDatabaseProvider.Configure` applies `.UseSnakeCaseNamingConvention()`
globally, and `ThemiaDbContext` *relies* on that global convention to produce its framework column
names (`tenant_id`, `created_at`, …). Two consequences: (a) the convention also rewrites the
adopter's own columns to snake_case, removing their freedom to use PascalCase on SQL Server; (b) the
framework's own columns are only correct as a side effect of a global setting — fragile.

**Solution.** Make the framework own its column names explicitly, and stop dictating the adopter's:

- In `ThemiaDbContext.OnModelCreating`, **explicitly map each framework-defined property to its fixed
  snake_case column** — applied in the existing `modelBuilder.Model.GetEntityTypes()` loop (alongside
  the tenant-id conversions and concurrency tokens) via
  `modelBuilder.Entity(clrType).Property(prop).HasColumnName(name)`. A property is mapped only when its
  **declaring** Themia type is present on the entity:

  | Declared by | Detect on `clrType` | Property → column |
  |---|---|---|
  | `Entity<TId>` (key) | derives from open generic `Entity<>` (walk `BaseType`) | `Id` → `id` |
  | `ITenantEntity` | `IsAssignableFrom` | `TenantId` → `tenant_id` |
  | `IAuditableEntity` | `IsAssignableFrom` | `CreatedAt` → `created_at`, `CreatedBy` → `created_by`, `LastModifiedAt` → `last_modified_at`, `LastModifiedBy` → `last_modified_by` |
  | `ISoftDeletable` | `IsAssignableFrom` | `IsDeleted` → `is_deleted`, `DeletedAt` → `deleted_at`, `DeletedBy` → `deleted_by`, `RestoredAt` → `restored_at`, `RestoredBy` → `restored_by` |
  | `IConcurrencyAware` | `IsAssignableFrom` | `RowVersion` → `row_version` |

  There is no `IEntity` interface — `Id` is declared on the abstract base class `Entity<TId>`, so the
  key mapping keys off "derives from `Entity<>`" via a small base-type-chain walk for the open generic
  (not "has an `Id` property", which would wrongly capture unrelated adopter types). Guard each
  `HasColumnName` on the property actually existing on the entity. These names match Dapper's
  `EntityMapping.ToSnakeCase` output exactly.

- **Themia maps only the columns of properties *it* defines** (the base-class `Id` + the marker-interface
  properties). It never touches an adopter's own declared properties. Cross-peer parity (EF and Dapper
  agreeing on column names) is therefore required, and provided, **only for framework columns** — and
  for framework module tables (Exceptional, Scheduling), which ship with explicit FluentMigrator schema.
  An adopter's own columns differing between EF (PascalCase on SQL Server) and Dapper (snake_case) is
  **not** a conflict: per DECISION #6 a single app uses a single peer, so only one convention ever
  governs the adopter's tables. Parity that matters is preserved; freedom that doesn't is left alone.

- **Remove the global `.UseSnakeCaseNamingConvention()` from the providers.** Replace it with an
  opt-in flag so the legacy behavior is recoverable:

  > **Amended post-review (2026-06-11):** the bool flag was dropped before release. Whole-model
  > snake_case is opted into via the standard EF mechanism instead — the adopter references
  > `EFCore.NamingConventions` themselves and passes
  > `configureOptions: o => o.UseSnakeCaseNamingConvention()`. This removes both the boolean public-API
  > parameter (repo code-quality rule) and the providers' unconditional `EFCore.NamingConventions`
  > dependency. See CHANGELOG 0.4.5. The text below documents the as-designed (superseded) shape.
  - Carry the flag on the provider itself: `PostgresDatabaseProvider` / `SqlServerDatabaseProvider`
    gain a `bool useGlobalSnakeCaseNaming = false` constructor argument, surfaced through an optional
    parameter on `AddThemiaPostgres` / `AddThemiaSqlServer`
    (`useGlobalSnakeCaseNaming: false` default). When `true`, `Configure` applies
    `.UseSnakeCaseNamingConvention()` as before (Postgres keeps `EFCore.NamingConventions`; SQL Server
    references it too only to honor this opt-in).
  - Default (`false`): the adopter's own columns follow EF's default convention — **PascalCase on SQL
    Server**, property-name-as-is on Postgres. Framework columns are snake_case regardless (explicit).

- **Behavior change (Postgres).** Existing Postgres consumers' *app* columns shift from forced
  snake_case to the EF default unless they set `UseGlobalSnakeCaseNaming = true`. Framework columns are
  unaffected. Recorded in `MIGRATION.md`. Pre-1.0, no external adopters.

**Table names.** Framework module tables (Exceptional, Scheduling) use explicit names already and are
unaffected. For adopter entities, table naming remains the adopter's responsibility via `ToTable` /
`DbSet` naming (EF) and `EntityMapping` overrides (Dapper); an adopter picks one peer per app
(DECISION #6), so cross-peer table-name parity is only required for framework tables, which are
explicit. No change needed here.

### 4. Engine specifics (inherited / already correct)

- **Concurrency token.** SQL Server is **already handled correctly** by the existing non-Npgsql branch
  in `ThemiaDbContext.ApplyConcurrencyTokens`: `byte[] RowVersion` + `IsRowVersion()` maps to the
  server-maintained `rowversion` column. No provider-specific code needed. (The documented MySQL
  landmine in that method remains a 0.4.6 concern, not this release.)
- **Timestamps.** EF SqlServer maps `DateTimeOffset`/`DateTime` to `datetime2` by default — consistent
  with the 0.4.4 Dapper SqlServer engine's `datetime2` choice. No override.
- **Tenant query filters, audit stamping, soft-delete filters, `Find` tenant post-check,
  `ValidateTenantWritesAsync`.** All live in the engine-agnostic `ThemiaDbContext` and are inherited
  unchanged.

### 5. Testing

New project `Themia.Framework.Data.EFCore.SqlServer.IntegrationTests`:

- Fixture: Testcontainers `MsSqlContainer` on `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04`
  (matching the Dapper SqlServer fixture). Schema built via `context.Database.EnsureCreatedAsync()`
  from the EF model — same mechanism the Postgres EF integration tests use (no FluentMigrator in this
  release).
- Mirror the Postgres EF integration suites against SQL Server: tenant isolation, audit tracking,
  soft-delete, and concurrency (`rowversion`). (A dedicated query-generation suite was descoped — the
  naming-split suite covers the SQL-shape concern via INFORMATION_SCHEMA.)
- **Add a naming-split assertion:** a context with a framework entity + an app entity, asserting (via
  `INFORMATION_SCHEMA.COLUMNS` or the EF model) that framework columns are snake_case (`tenant_id`,
  `created_at`, …) while the app entity's own column is PascalCase on SQL Server.
- The PG-extraction refactor is covered by the **existing** EFCore integration/unit tests remaining
  green after Postgres moves to its own package (only namespaces/package refs change, not behavior).
  Add/adjust a unit test for the scoped-naming change (framework columns mapped without the global
  convention).

### 6. Release

- `Directory.Build.props` `<Version>` `0.4.4` → `0.4.5`.
- `CHANGELOG.md` `## 0.4.5 — 2026-06-11`:
  - **Added** — `Themia.Framework.Data.EFCore.SqlServer` (SQL Server EF provider + `AddThemiaSqlServer`);
    `Themia.Framework.Data.EFCore.PostgreSql` (Postgres provider, extracted from core).
  - **Changed** — `Themia.Framework.Data.EFCore` is now provider-agnostic (no bundled provider deps);
    framework columns are explicitly mapped to snake_case in `ThemiaDbContext`; the global snake_case
    naming convention is no longer applied by the providers (superseded — adopters opt in via
    `configureOptions: o => o.UseSnakeCaseNamingConvention()` with their own package reference) so adopter columns follow
    EF defaults (PascalCase on SQL Server).
- `MIGRATION.md` — (a) `AddThemiaPostgres` moved to the `Themia.Framework.Data.EFCore.PostgreSql`
  package (add ref + `using`); (b) Postgres app-column naming now defaults to EF convention — set
  `configureOptions: o => o.UseSnakeCaseNamingConvention()` (with an `EFCore.NamingConventions`
  reference) to keep forced snake_case — superseded per the §3 amendment.
- `Themia.sln` — add 3 projects: `Themia.Framework.Data.EFCore.PostgreSql`,
  `Themia.Framework.Data.EFCore.SqlServer`, `Themia.Framework.Data.EFCore.SqlServer.IntegrationTests`.
- Each new src package: `PublicAPI.{Shipped,Unshipped}.txt`, `PublicApiAnalyzer` reference,
  `InternalsVisibleTo` for its test project where needed.

## Out-of-scope / follow-ups (tracked, not built here)

- EF MySQL provider + the `ApplyConcurrencyTokens` MySQL branch (the documented landmine) → 0.4.6.
- FluentMigrator as single schema authority (Scheduling EF-migration rewrite + aggregating runner +
  per-provider concurrency-token DDL) → 0.4.7.
- Raw-connection escape-hatch hardening + `Themia.Analyzers` rule (Dapper-as-peer gate) → 0.4.8.

## Success criteria

- `AddThemiaSqlServer<TContext>` registers a working tenant-isolating, audit-stamping, soft-deleting
  EF context on SQL Server, sharing all `ThemiaDbContext` behavior with the Postgres peer.
- Core `Themia.Framework.Data.EFCore` has no provider package dependency; PG and SqlServer providers
  are sibling packages.
- Framework columns are snake_case on SQL Server (matching the Dapper SqlServer engine); an adopter's
  own entity columns are PascalCase on SQL Server.
- All existing EFCore tests green after the Postgres extraction; new SqlServer integration suite green.
