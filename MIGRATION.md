# Migration Guide

Upgrade notes and breaking-change guidance between **Themia** versions. Every
**(breaking)** entry in [CHANGELOG.md](CHANGELOG.md) has a matching section here
with the *why* and concrete upgrade steps.

## How to read this guide

- Sections are ordered **newest first**, headed by the version that introduced the change.
- Each entry states: **What changed**, **Why**, and **How to upgrade** (before → after).
- Non-breaking changes are *not* listed here — see the CHANGELOG.

## 0.4.7

### Scheduling module: schema via FluentMigrator + requires an EF provider

**What changed:** `Themia.Modules.Scheduling` applies its schema with FluentMigrator at `InitializeAsync`
(through `Themia.Data.Migrations`) instead of EF Core migrations, and is now provider-agnostic over
PostgreSQL and SQL Server. It resolves the active `IDatabaseProvider` for both the EF provider and the
migration engine.

**Why:** FluentMigrator is the single schema authority (DECISION #6); the module is no longer PostgreSQL-only.

**How to upgrade:**

- Ensure an EF provider is registered before the module initializes — `AddThemiaPostgres<…>(…)` or
  `AddThemiaSqlServer<…>(…)`. Without one, the module throws at startup.
- Stop running `dotnet ef database update` for the scheduling context; the schema is applied automatically
  on startup. The table shapes are unchanged, so existing PostgreSQL databases are compatible (the FM
  migration creates the same `scheduling.execution_history` / `scheduling.scheduler_stats`).
- SQL Server is now supported.

## 0.4.6

### `AddThemiaExceptionalProvider` takes a `MigrationEngine`

**What changed:** the provider-author extension `AddThemiaExceptionalProvider` (in `Themia.Exceptional`)
replaced its `Action<IMigrationRunnerBuilder> configureRunner` + `string databaseDisplayName` parameters
with a single `Themia.Data.Migrations.MigrationEngine engine`.

**Why:** the FluentMigrator runner moved into the neutral `Themia.Data.Migrations` package so every
neutral core and framework module shares one runner (DECISION #6). The engine enum replaces the
per-call runner-builder callback.

**Who is affected:** only third parties that call `AddThemiaExceptionalProvider` directly to back a
custom dialect. Adopters using `AddThemiaExceptionalPostgres` / `…MySql` / `…SqlServer` are unaffected.

**How to upgrade:**

- Before:
  ```csharp
  services.AddThemiaExceptionalProvider(
      dialect: myDialect,
      configure: opt => opt.ApplicationName = "App",
      configureRunner: rb => rb.AddPostgres(),
      connectionString: connString,
      databaseDisplayName: "PostgreSQL");
  ```
- After:
  ```csharp
  using Themia.Data.Migrations;

  services.AddThemiaExceptionalProvider(
      dialect: myDialect,
      configure: opt => opt.ApplicationName = "App",
      engine: MigrationEngine.Postgres,
      connectionString: connString);
  ```

## 0.4.5

### `AddThemiaPostgres` moved to `Themia.Framework.Data.EFCore.PostgreSql`

**What changed:** the core EF package (`Themia.Framework.Data.EFCore`) is now provider-agnostic.
`PostgresDatabaseProvider` and `AddThemiaPostgres` live in the new
`Themia.Framework.Data.EFCore.PostgreSql` package; core no longer references Npgsql.

**Why:** per-engine provider packages (mirroring the Dapper layer, DECISION #6) — consumers pull
only the engine they use instead of every provider's dependencies.

**How to upgrade:**

- Before:
  ```csharp
  // package: Themia.Framework.Data.EFCore
  using Themia.Framework.Data.EFCore.Extensions;
  services.AddThemiaPostgres<AppDbContext>(configuration);
  ```
- After:
  ```csharp
  // packages: Themia.Framework.Data.EFCore.PostgreSql (core comes transitively)
  using Themia.Framework.Data.EFCore.PostgreSql;
  services.AddThemiaPostgres<AppDbContext>(configuration);
  ```

### `AddThemiaDbContextWithProvider` removed

**What changed:** the string-name provider factory (`AddThemiaDbContextWithProvider(configuration,
"postgres")`) was removed from core.

**Why:** core can no longer construct provider types it does not reference; each provider package
ships its own type-safe entry point.

**How to upgrade:** call the per-engine extension directly — `AddThemiaPostgres<TContext>(…)`
(`Themia.Framework.Data.EFCore.PostgreSql`) or `AddThemiaSqlServer<TContext>(…)`
(`Themia.Framework.Data.EFCore.SqlServer`).

### App-table columns are no longer forced to snake_case

**What changed:** the providers no longer apply `UseSnakeCaseNamingConvention()` to the whole model
by default. Themia's framework columns (`id`, `tenant_id`, `created_at`, `is_deleted`,
`row_version`, …) are now explicitly mapped to snake_case in `ThemiaDbContext` regardless; your own
entities' columns follow the EF provider default (property name as-is — PascalCase on SQL Server).

**Why:** Themia owns the naming of *its* columns (parity with the Dapper layer and one
FluentMigrator schema across engines) but should not dictate the adopter's app-table naming.

**How to upgrade:**

- If your existing PostgreSQL schema has snake_case **app** columns (the previous forced behavior),
  reference `EFCore.NamingConventions` in your app and re-apply the convention via the registration
  delegate:
  ```csharp
  services.AddThemiaPostgres<AppDbContext>(
      configuration,
      configureOptions: o => o.UseSnakeCaseNamingConvention());
  ```
- New apps (and SQL Server apps wanting idiomatic PascalCase) need no change — no global convention
  is applied by default, and the provider packages no longer depend on `EFCore.NamingConventions`.

## Template

````markdown
## x.y.z

### <short title of the breaking change>

**What changed:** …

**Why:** …

**How to upgrade:**

- Before:
  ```csharp
  // old usage
  ```
- After:
  ```csharp
  // new usage
  ```
````
