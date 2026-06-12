# Themia 0.4.7 — Scheduling EF→FluentMigrator (Postgres + SQL Server) (design)

> Spec. Master context: [`themia-architecture-overview.md`](../../themia-architecture-overview.md)
> (DECISION #6 — FluentMigrator is the single schema authority; no `dotnet ef migrations add`).
> Builds on 0.4.6's neutral runner [`Themia.Data.Migrations`](2026-06-12-themia-data-migrations-runner-design.md).
> Status date: 2026-06-12.

## Goal

Move `Themia.Modules.Scheduling`'s own schema (its two tables) off **EF Core migrations** onto
**FluentMigrator**, applied through the shared `ThemiaMigrations.Run` runner, and make the module
**provider-agnostic over PostgreSQL and SQL Server** (it is PostgreSQL-only today). This is the first
module to consume the 0.4.6 runner and the first proof that DECISION #6's "FluentMigrator owns all
framework schema" holds for an EF-backed module across more than one engine.

## Scope (decided)

The 0.4.x roadmap item "Scheduling EF→FM + persistent Quartz" is **split** (user-approved):

- **0.4.7 (this spec):** Scheduling EF→FM, provider-agnostic over Postgres + SQL Server.
- **0.4.8 (deferred, own spec):** persistent Quartz — `AddQuartz().UsePersistentStore()` **default-on**,
  the 11 `qrtz_*` tables authored as per-engine FluentMigrator migrations, `UseSystemTextJsonSerializer()`
  (no Newtonsoft), and the decision of Themia owning the `IScheduler` (today the host owns it; Themia wires
  only the dashboard).
- **0.4.9 (deferred):** raw-connection + `DbSet.Find`-on-tracked analyzer gate (the Dapper-as-peer guard).

**Explicitly out of 0.4.7:** anything Quartz/`AdoJobStore`/`qrtz_*`/scheduler ownership (0.4.8); **MySQL**
(no EF MySQL provider exists — Pomelo has no EF Core 10 build; arrives with the EF-MySQL release); the
`EfExecutionHistoryStore` raw SQL `scheduling.scheduler_stats` schema-qualifier, which only breaks on MySQL
(no schema namespace) and is therefore deferred with MySQL; FluentMigrator 6→8.

## Background (current state)

`src/modules/Themia.Modules.Scheduling/` implements `IThemiaModule` (via `ThemiaModuleBase`) and is
**hard-wired to PostgreSQL**:

- `SchedulingModule.ConfigureServices` registers `AddDbContextFactory<SchedulingDbContext>` with a
  hard-coded `dbOptions.UseNpgsql(connectionString).UseSnakeCaseNamingConvention()`; the connection string
  is `IConfiguration.GetConnectionString("Default")`.
- `SchedulingModule.InitializeAsync` opens a scope, resolves `SchedulingDbContext`, and calls
  `await context.Database.MigrateAsync(ct)` — **this is the EF-migration call to replace.**
- `SchedulingDbContext : ThemiaDbContext` maps two tables in the `scheduling` schema with
  `EnableTenantFilters = false` / `EnableSoftDeleteFilters = false` (execution history is process-wide
  infrastructure, not tenant-scoped). Column names derive from `UseSnakeCaseNamingConvention()` (the model
  sets `ToTable(...)` + `HasMaxLength(...)`/`IsRequired()` but **not** explicit `HasColumnName`).
- The two tables:
  - **`scheduling.execution_history`** — PK `fire_instance_id` `varchar(256)`; `scheduler_instance_id`
    `varchar(256)` null; `scheduler_name` `varchar(256)` null; `job` `varchar(512)` null; `trigger`
    `varchar(512)` null; `scheduled_fire_time_utc` `DateTimeOffset?`; `actual_fire_time_utc` `DateTimeOffset`
    NOT NULL; `recovering` bool; `vetoed` bool; `finished_time_utc` `DateTimeOffset?`; `exception_message`
    `varchar(4000)` null. Index `ix_execution_history_scheduler_trigger_fired` on
    (`scheduler_name`, `trigger`, `actual_fire_time_utc`).
  - **`scheduling.scheduler_stats`** — PK `scheduler_name` `varchar(256)`; `total_jobs_executed` int;
    `total_jobs_failed` int.
- EF-migration artifacts: `Migrations/20260607003329_InitialScheduling.cs` (+ `.Designer.cs`),
  `Migrations/SchedulingDbContextModelSnapshot.cs`, and the design-time `SchedulingDbContextFactory.cs`.

The framework already exposes most of what the provider-agnostic rewrite needs:
`IDatabaseProvider.ProviderName` returns `DatabaseProviderNames.{Postgres="postgres",SqlServer="sqlserver",MySql="mysql"}`;
implementations `PostgresDatabaseProvider` / `SqlServerDatabaseProvider` exist (no EF MySQL provider). **Gap:**
`AddThemiaDbContext` (what `AddThemiaPostgres`/`AddThemiaSqlServer` call) currently captures the provider in a
closure to configure the DbContext and **does not register it in DI** — so a module cannot resolve the active
provider today. 0.4.7 closes this with a one-line additive change (§0 below).

## Architecture

### 0. Framework — make the active `IDatabaseProvider` resolvable

`Themia.Framework.Data.EFCore`'s `AddThemiaDbContext<TContext>(IDatabaseProvider provider, …)` gains one line:
`services.TryAddSingleton<IDatabaseProvider>(provider)` (alongside the existing `provider.ConfigureServices(...)`
call). This makes the app's chosen provider discoverable by any module that needs to know the active engine —
the mechanism the approved design relies on. `TryAdd` keeps the first registration if an app registers more
than one Themia context (they share one engine). Additive and behavior-only; no public-API/type change.

### 1. Schema authority — one FluentMigrator migration replaces the EF migration

A new `[Migration]` class (`SchedulingSchemaMigration`) in the Scheduling module assembly, mirroring
`Themia.Exceptional/Migrations/ExceptionLogMigration.cs`'s **LOCKSTEP** pattern:

- `IfDatabase("postgres", "sqlserver").Delegate(() => CreateSchemaAndTables())` — `Create.Schema("scheduling")`
  then the two tables. Columns: `AsString(n)` for the varchars, `AsBoolean()` for the flags, `AsInt32()`
  for the counters, and **`AsDateTimeOffset()`** for the three timestamp columns — FluentMigrator maps
  `AsDateTimeOffset()` to `timestamptz` on Postgres and `datetimeoffset` on SQL Server, matching what EF
  emits for the `DateTimeOffset` properties on each engine, so **no per-engine timestamp branch is needed.**
  Column names are authored **explicitly snake_case** (`fire_instance_id`, …) to match the DbContext's
  snake_case convention. PKs and the composite index reproduce the current schema exactly.
- `IfDatabase(p => !p.StartsWith("Postgres", OrdinalIgnoreCase) && !p.StartsWith("SqlServer", OrdinalIgnoreCase))
  .Delegate(() => throw new NotSupportedException(...))` — the unsupported-engine guard (the LOCKSTEP twin of
  the create whitelist; edit both together when MySQL is added later).
- `Down()` drops the two tables and the schema.

The migration is version-stamped (e.g. `[Migration(202606120001, "Themia.Scheduling: create scheduling tables")]`,
the exact number finalized in the plan); its version must be unique within the assembly (the runner throws on
duplicates).

### 2. Provider-agnostic DbContext registration + engine detection

`SchedulingModule.ConfigureServices` registers the DbContext factory based on the app's **registered
`IDatabaseProvider`** rather than hard-coding Npgsql:

```csharp
services.AddDbContextFactory<SchedulingDbContext>((sp, dbOptions) =>
{
    var provider = sp.GetRequiredService<IDatabaseProvider>();
    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' was not found; ...");

    switch (provider.ProviderName)
    {
        case DatabaseProviderNames.Postgres:  dbOptions.UseNpgsql(connectionString);     break;
        case DatabaseProviderNames.SqlServer: dbOptions.UseSqlServer(connectionString);  break;
        default: throw new NotSupportedException(
            $"Themia.Scheduling supports PostgreSQL and SQL Server; provider '{provider.ProviderName}' is not supported.");
    }
    dbOptions.UseSnakeCaseNamingConvention();   // identical snake_case names on both engines → matches the FM schema
});
```

A small internal mapper bridges the framework's provider name to the neutral runner's enum:

```csharp
private static MigrationEngine ToMigrationEngine(string providerName) => providerName switch
{
    DatabaseProviderNames.Postgres  => MigrationEngine.Postgres,
    DatabaseProviderNames.SqlServer => MigrationEngine.SqlServer,
    _ => throw new NotSupportedException($"Themia.Scheduling supports PostgreSQL and SQL Server; '{providerName}' is not supported."),
};
```

This lives in the Scheduling module (it references both the framework `DatabaseProviderNames` and the neutral
`MigrationEngine` — the neutral runner cannot reference the framework). Promote it to a shared helper only if a
second module needs it (YAGNI).

### 3. `InitializeAsync` runs the FM migration through the shared runner

Replaces `MigrateAsync` with a synchronous `ThemiaMigrations.Run` call (the runner is sync; `InitializeAsync`
returns a completed `ValueTask`):

```csharp
public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
{
    ArgumentNullException.ThrowIfNull(serviceProvider);
    using var scope = serviceProvider.CreateScope();
    var provider = scope.ServiceProvider.GetRequiredService<IDatabaseProvider>();
    var connectionString = scope.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' was not found; ...");

    ThemiaMigrations.Run(ToMigrationEngine(provider.ProviderName), connectionString, typeof(SchedulingSchemaMigration).Assembly);
    return ValueTask.CompletedTask;
}
```

### 4. Connection semantics — app-global

Scheduling stays a **single shared store**: it always uses the **`Default`** connection string and keeps
tenant/soft-delete filters off (unchanged intent). It deliberately does **not** route through
`DatabaseConnectionStringResolver` (the tenant-aware path) — execution history and scheduler stats are
process-wide infrastructure, so in a DB-per-tenant app they live once in the shared/Default database, never
per-tenant. The module reuses the active provider only to learn the **engine**, not the connection.

### 5. EF-migration cleanup

Delete `Migrations/20260607003329_InitialScheduling.cs`, `…InitialScheduling.Designer.cs`,
`Migrations/SchedulingDbContextModelSnapshot.cs`, and `SchedulingDbContextFactory.cs`; drop the
`Microsoft.EntityFrameworkCore.Design` PackageReference (no more `dotnet ef migrations`). Keep
`SchedulingDbContext` (runtime store) and `EfExecutionHistoryStore` unchanged. The store's raw SQL keeps the
`scheduling.scheduler_stats` qualifier — valid on Postgres and SQL Server (both have schemas); the MySQL break
is deferred with MySQL.

### 6. Project references

`Themia.Modules.Scheduling.csproj`: add `<ProjectReference Include="…/Themia.Data.Migrations/…csproj" />`
and `<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />` (to call `UseSqlServer`; it
already references Npgsql for `UseNpgsql`). Keep `EFCore.NamingConventions` (for `UseSnakeCaseNamingConvention`)
and `FluentMigrator` (for the `[Migration]` type). `FluentMigrator.Runner` flows transitively via the runner
package.

## Testing

`tests/Themia.Modules.Scheduling.IntegrationTests` (Postgres-only today) becomes multi-engine:

- The current `InitializeAsync_RunsMigration_AndStoreRoundTrips` test asserts `GetPendingMigrationsAsync()`
  is empty — an **EF-migration-specific** assertion that breaks once FM owns the schema. Replace it with a
  **table-existence** check (query the engine's catalog for `scheduling.execution_history`) plus the existing
  store round-trip (`Save` → `Get`, `ExecutionHistory.CountAsync()`).
- Parameterize the suite over **both engines**: keep the `postgres:16-alpine` container and add a SQL Server
  Testcontainers container (`mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04`, matching the Exceptional
  SQL Server suite). For each engine the test must **register the matching `IDatabaseProvider`**
  (`PostgresDatabaseProvider` / `SqlServerDatabaseProvider`) so `ConfigureServices`/`InitializeAsync` resolve
  it — the old test relied on the hard-coded `UseNpgsql` and registered no provider.
- The `ConfigureServices_RegistersStoreAndDashboardOptions` and `DefaultAuthorize_*` tests stay (engine-agnostic),
  though the store registration now also requires an `IDatabaseProvider` in the container.
- Add `Testcontainers.MsSql` to the test csproj (already pinned in `Directory.Packages.props`).

## Release

- `Directory.Build.props` `<Version>` `0.4.6` → `0.4.7`.
- `CHANGELOG.md` `## 0.4.7`:
  - **Changed** — `Themia.Modules.Scheduling` now creates its schema via FluentMigrator through the shared
    `Themia.Data.Migrations` runner (DECISION #6) instead of EF Core migrations, and is provider-agnostic over
    PostgreSQL and SQL Server (was PostgreSQL-only). It now requires a registered EF `IDatabaseProvider`
    (`AddThemiaPostgres`/`AddThemiaSqlServer`).
  - **Removed** — the module's EF migration artifacts + design-time factory (schema is FluentMigrator-owned).
- `MIGRATION.md` `## 0.4.7`: the module's schema is now applied automatically by `InitializeAsync` via
  FluentMigrator; adopters who previously ran `dotnet ef database update` for the scheduling context stop doing
  so. SQL Server adopters newly supported. The module now requires an EF provider registration.
- Reconcile the roadmap across the four docs (arch-overview DECISION #6 spawn line + the per-provider/Quartz
  notes, the 0.4.5 spec, the release-strategy spec, and the 0.4.6 spec/plan) to: **0.4.7** Scheduling EF→FM →
  **0.4.8** persistent Quartz (`AdoJobStore` + `qrtz_*` + STJ) → **0.4.9** raw-connection + `DbSet.Find` analyzer
  gate; EF MySQL deferred.

## Success criteria

- `Themia.Modules.Scheduling` creates its two tables via `SchedulingSchemaMigration` run through
  `ThemiaMigrations.Run` — no `context.Database.MigrateAsync()`, no EF migration files remain.
- The module runs on **both** PostgreSQL and SQL Server, selecting engine + `MigrationEngine` from the active
  `IDatabaseProvider`; fails fast with a clear message when no provider is registered or the provider is
  unsupported.
- Integration suite green on both engines (FM migration applies → `EfExecutionHistoryStore` round-trips); the
  EF-migration-coupled assertion is gone.
- Clean `dotnet build Themia.sln --no-incremental` (TreatWarningsAsErrors, PublicAPI recorded).
