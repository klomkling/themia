# Themia 0.4.8 — persistent Quartz (AdoJobStore, PostgreSQL + SQL Server) (design)

> Spec. Master context: [`themia-architecture-overview.md`](../../themia-architecture-overview.md)
> (DECISION #6 — FluentMigrator is the single schema authority). Builds on 0.4.6's shared runner
> [`Themia.Data.Migrations`](2026-06-12-themia-data-migrations-runner-design.md) and 0.4.7's
> [Scheduling EF→FM](2026-06-12-themia-scheduling-fluentmigrator-design.md). Status date: 2026-06-12.

## Goal

Make Quartz.NET **persistent and default-on**: `Themia.Modules.Scheduling` registers a Quartz scheduler
backed by **AdoJobStore**, with the `qrtz_*` tables authored as a per-engine **FluentMigrator** migration (run
through the shared `ThemiaMigrations.Run`), `UseSystemTextJsonSerializer()` (no Newtonsoft), for **PostgreSQL +
SQL Server**. This is the third 0.4.x FluentMigrator-authority slice and completes the "Scheduling EF→FM +
persistent Quartz" roadmap item (split from 0.4.7).

## Background (current state)

- **Themia owns no scheduler.** `Themia.Quartz` (neutral, `net8.0;net10.0`, Quartz 3.18.1) is **dashboard-only**:
  `AddThemiaQuartz` registers `ThemiaQuartzOptions` + the vendored SilkierQuartz dashboard MVC parts; nothing
  builds, registers, or starts an `IScheduler`. `UseThemiaQuartz`/`MapThemiaQuartz` resolves the scheduler at
  mount time via `ThemiaQuartzOptions.Scheduler ?? serviceProvider.GetService<IScheduler>()` and throws if
  neither exists. The host supplies the scheduler today.
- **No module orchestrator.** `IThemiaModule.InitializeAsync` is defined but **nothing in the framework calls
  it** — the host wires modules manually (`module.ConfigureServices(services)` then
  `await module.InitializeAsync(sp)`, before `app.Run()`). Themia therefore controls run-ordering itself.
- **Two independent persistence layers (post-0.4.8).** Quartz's **AdoJobStore** (`qrtz_*` — job/trigger
  definitions + scheduler state) is orthogonal to Themia's **execution-history** store (`scheduling.*` —
  job-run history, via `EfExecutionHistoryStore` + `ExecutionHistoryPlugin`). 0.4.8 adds AdoJobStore; the
  history layer is unchanged.
- **Migration plumbing exists.** `ThemiaMigrations.Run(MigrationEngine, connectionString, params Assembly[])`
  applies all `[Migration]` types in the scanned assembly, FM-`VersionInfo`-idempotent. `SchedulingSchemaMigration`
  is the per-engine template (`IfDatabase("postgres","sqlserver")` + LOCKSTEP guard).
- **Missing packages.** `Quartz.Serialization.SystemTextJson` and `Quartz.Extensions.Hosting` (both 3.18.1 on
  nuget.org) are not yet referenced; the AdoJobStore data providers ship in the `Quartz` package itself.

## Decisions (from brainstorming)

- **Default-on, host can opt out.** Persistent AdoJobStore is registered by default. A new
  `SchedulingModuleOptions.UsePersistentStore` (default `true`) set to `false` registers **no** scheduler — the
  host keeps today's host-supplied behavior (`ThemiaQuartzOptions.Scheduler`). Opt-out gates only the **scheduler
  registration**, not the migration (see Run-ordering).
- **Dedicated `quartz` schema.** The 11 tables are created in a `quartz` schema (lowercase, `qrtz_` prefix);
  AdoJobStore's table prefix is schema-qualified (`quartz.qrtz_`). Isolated from `public`/`dbo`, consistent with
  the module's `scheduling` schema.
- **Greenfield migration (no existence guards).** Themia owns the `quartz` schema exclusively; unlike 0.4.7's
  EF→FM cutover there is no pre-existing-table case, so plain creates + FM-`VersionInfo` idempotency suffice
  (matches `ExceptionLogMigration`).
- **Non-clustered, single-instance.** Clustering (`UseClustering`, multi-instance) is deferred (YAGNI).
- **`UseProperties = true`.** JobDataMap stored as string key-values (Quartz's recommended AdoJobStore default —
  avoids type-versioning hazards); the STJ serializer handles calendars. Documented constraint.
- **PostgreSQL + SQL Server only.** MySQL deferred with the EF MySQL provider (Pomelo has no EF Core 10 build).

## Architecture

### 1. The `qrtz_*` FluentMigrator migration

A new `QuartzAdoJobStoreMigration` in `src/modules/Themia.Modules.Scheduling/Migrations/`, mirroring
`SchedulingSchemaMigration`'s LOCKSTEP shape:

- `[Migration(202606130001, "Themia.Scheduling: create Quartz AdoJobStore (qrtz_*) schema")]` (exact stamp
  finalized in the plan), unique within the module assembly (distinct from `SchedulingSchemaMigration`'s `202606120001`).
- `IfDatabase("postgres").Delegate(CreatePostgres)` + `IfDatabase("sqlserver").Delegate(CreateSqlServer)` +
  the LOCKSTEP unsupported-engine guard (`IfDatabase(p => !Postgres && !SqlServer).Delegate(throw NotSupportedException)`).
- **Per-engine create methods** are a faithful hand-port of Quartz 3.18.1's `database/tables/tables_postgres.sql`
  and `tables_sqlServer.sql` (no in-repo copy exists — author from the canonical 3.18.x DDL), into the `quartz`
  schema with lowercase `qrtz_` identifiers. The 11 tables: `qrtz_job_details`, `qrtz_triggers`,
  `qrtz_simple_triggers`, `qrtz_cron_triggers`, `qrtz_simprop_triggers`, `qrtz_blob_triggers`, `qrtz_calendars`,
  `qrtz_paused_trigger_grps`, `qrtz_fired_triggers`, `qrtz_scheduler_state`, `qrtz_locks`.
- **Per-engine type landmines** (why a single CreateTable cannot serve both, unlike the history schema):
  - BLOB columns (`qrtz_job_details.job_data`, `qrtz_triggers.job_data`, `qrtz_calendars.calendar`,
    `qrtz_blob_triggers.blob_data`, `qrtz_simprop_triggers.*`) → `bytea` (PG) vs `varbinary(max)`/`image`
    (SQL Server). Use FM `AsBinary()`/`AsCustom("bytea")` per branch.
  - Boolean flags (`is_durable`, `is_nonconcurrent`, `is_update_data`, `requests_recovery`) → `bool` (PG) vs
    `bit` (SQL Server) — `AsBoolean()` maps both; verify against Quartz's delegate expectations.
  - Time columns are **bigint** epoch-millis (`next_fire_time`, `prev_fire_time`, `start_time`, `end_time`,
    `fired_time`, `sched_time`, etc.) — **not** datetimeoffset. `AsInt64()`.
  - Composite primary keys keyed on `sched_name` (every table); FKs `qrtz_triggers → qrtz_job_details` and the
    sub-trigger tables → `qrtz_triggers` on `(sched_name, trigger_name, trigger_group)`. Replicate Quartz's
    column sets and constraint names.
- Greenfield: plain `Create.Schema("quartz")` + `Create.Table(...)`; no existence guards.

`InitializeAsync` is unchanged — `ThemiaMigrations.Run(ToMigrationEngine(provider.ProviderName), connectionString,
typeof(SchedulingSchemaMigration).Assembly)` already scans the whole module assembly, so it applies both the
history migration and the new `qrtz_*` migration in version order.

### 2. Scheduler registration (default-on, opt-out)

In `SchedulingModule.ConfigureServices`, when `options.UsePersistentStore` is `true` (default):

```csharp
services.AddQuartz(q =>
{
    q.SchedulerName = options.SchedulerName;
    q.UsePersistentStore(s =>
    {
        s.UseProperties = true;
        s.UseSystemTextJsonSerializer();
        // engine picked from the active IDatabaseProvider (same switch as the DbContext factory),
        // schema-qualified table prefix "quartz.qrtz_", Default connection string:
        //   Postgres  -> s.UsePostgres(...)
        //   SqlServer -> s.UseSqlServer(...)
    });
    // register the execution-history listener on Themia's scheduler
    q.AddJobListener<ExecutionHistoryPlugin>(EverythingMatcher<JobKey>.AllJobs());
});
services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
```

The exact Quartz 3.18 API for the per-engine data provider + the schema-qualified table prefix is finalized in
the plan; the design intent is: engine from `IDatabaseProvider.ProviderName`, `Default` connection, STJ
serializer, `UseProperties = true`, prefix `quartz.qrtz_`, non-clustered. The `ExecutionHistoryPlugin` **listener**
(today added by the host) is now registered by Themia in the `AddQuartz` builder. The store→`scheduler.Context`
bridge **stays in `UseThemiaQuartz`** (`scheduler.Context.SetExecutionHistoryStore(store)`) unchanged — it already
resolves the DI scheduler (now Themia's) and the registered `IExecutionHistoryStore`.

When `UsePersistentStore` is `false`: register nothing Quartz-scheduler-related (today's behavior); the host
supplies `ThemiaQuartzOptions.Scheduler` or a DI `IScheduler`, which `UseThemiaQuartz` resolves unchanged.

### 3. Scheduler resolution & dashboard (neutral core unchanged)

`Themia.Quartz` is **not modified** for the scheduler. With Themia registering `AddQuartz`, a DI `IScheduler`
exists, so the existing `UseThemiaQuartz` fallback (`options.Scheduler ?? GetService<IScheduler>()`) resolves
Themia's scheduler automatically. The neutral core keeps its net8 leg DB-free.

### 4. Run-ordering

`AddQuartzHostedService` starts the scheduler as an `IHostedService` during `app.Run()`/`StartAsync`. The host's
standard sequence — wire modules (`ConfigureServices`) → build → `await module.InitializeAsync(sp)` (runs the
`qrtz_*` migration) → `app.Run()` — guarantees the tables exist before the hosted service connects. If a host
omits `InitializeAsync`, the AdoJobStore start fails loudly on missing tables (documented in MIGRATION.md). No
framework module-orchestrator is introduced in 0.4.8.

### 5. Packages

Add to `Directory.Packages.props` (pinned at 3.18.1, matching `Quartz`): `Quartz.Serialization.SystemTextJson`,
`Quartz.Extensions.Hosting`. Reference both from `Themia.Modules.Scheduling.csproj`. The AdoJobStore data
providers (`UsePostgres`/`UseSqlServer`) are in the `Quartz` package; the ADO drivers (`Npgsql`,
`Microsoft.Data.SqlClient`) already reach the module transitively via its EF providers.

## Testing

Extend `tests/Themia.Modules.Scheduling.IntegrationTests` (the engine-parameterized PG + SQL Server
Testcontainers harness):

- **Schema present:** after `InitializeAsync`, assert the `quartz` schema and the 11 `qrtz_*` tables exist (per
  engine), via the same catalog-query pattern the 0.4.7 tests use (engine-specific, in the abstract per-engine
  members).
- **Persistence round-trip (the headline test):** build the module services, `InitializeAsync` (migrates),
  resolve `ISchedulerFactory`, start a scheduler, schedule a **durable** job + trigger, shut the scheduler down,
  then build a **second** service provider over the **same** container database, start a new scheduler, and
  assert the job/trigger are still present (loaded from `qrtz_*`). Proves AdoJobStore persistence across a
  process "restart" without a web host. Per engine.
- **Opt-out:** with `UsePersistentStore = false`, assert no `IScheduler`/`ISchedulerFactory` is registered by the
  module (host-supplied path intact).
- `Themia.Quartz.Tests` (dashboard, in-proc history, JSON) unchanged.

## Out of scope

Clustering / multi-instance (`UseClustering`); MySQL (with EF MySQL); a framework module-orchestrator that
auto-invokes `InitializeAsync`; exposing `UseProperties`/serializer/table-prefix as adopter options (sensible
defaults only); the 0.4.9 raw-connection + `DbSet.Find` analyzer gate.

## Release

- `Directory.Build.props` `<Version>` `0.4.7` → `0.4.8`.
- `CHANGELOG.md` `## 0.4.8`: **Added** — `Themia.Modules.Scheduling` now registers a **persistent Quartz
  scheduler (AdoJobStore)** by default, with the `qrtz_*` schema authored as a FluentMigrator migration
  (`quartz` schema; PostgreSQL + SQL Server) and `UseSystemTextJsonSerializer()`. Set
  `SchedulingModuleOptions.UsePersistentStore = false` to keep a host-supplied scheduler.
- `MIGRATION.md` `## 0.4.8`: the module now owns + starts a scheduler by default (was host-supplied); the host
  must call `InitializeAsync` before running (so the `qrtz_*` tables exist before the scheduler starts); JobDataMap
  is string-only (`UseProperties = true`); `UsePersistentStore = false` to opt out.
- Reconcile the roadmap (arch-overview + the 0.4.x specs): **0.4.8** persistent Quartz → **0.4.9** raw-connection
  + `DbSet.Find` analyzer gate; EF MySQL deferred.

## Success criteria

- `Themia.Modules.Scheduling` registers a default-on AdoJobStore scheduler; the `qrtz_*` schema is created in a
  `quartz` schema via FluentMigrator (PG + SQL Server) through `ThemiaMigrations.Run`; no Newtonsoft (STJ
  serializer).
- A scheduled durable job **survives a scheduler restart** (persistence round-trip green on both engines).
- `UsePersistentStore = false` cleanly restores host-supplied scheduling.
- Clean `dotnet build Themia.sln --no-incremental` (TreatWarningsAsErrors, PublicAPI recorded); full suite green.
