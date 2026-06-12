# Persistent Quartz (AdoJobStore, PostgreSQL + SQL Server) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `Themia.Modules.Scheduling` register a default-on persistent Quartz scheduler (AdoJobStore) whose `qrtz_*` schema is created by a FluentMigrator migration (a `quartz` schema, PostgreSQL + SQL Server) and serialized with System.Text.Json, with a host opt-out.

**Architecture:** A new `QuartzAdoJobStoreMigration` (in the module assembly, picked up by the existing `ThemiaMigrations.Run` scan in `InitializeAsync`) ports Quartz 3.18.1's canonical `tables_postgres.sql`/`tables_sqlServer.sql` into a `quartz` schema via `IfDatabase(...).Delegate(() => Execute.Sql(...))`. `SchedulingModule.ConfigureServices` registers `AddQuartz().UsePersistentStore(...)` + `AddQuartzHostedService` when `SchedulingModuleOptions.UsePersistentStore` is true (default); the neutral `Themia.Quartz` core is untouched and resolves the DI scheduler via its existing fallback.

**Tech Stack:** .NET 10, Quartz.NET 3.18.1 (+ `Quartz.Serialization.SystemTextJson`, `Quartz.Extensions.Hosting`), FluentMigrator 6.2.0 via `Themia.Data.Migrations`, EF Core 10, xUnit, Testcontainers (PostgreSql + MsSql).

**Reference spec:** `docs/superpowers/specs/2026-06-12-themia-persistent-quartz-design.md`

---

## File Structure

**New:**
- `src/modules/Themia.Modules.Scheduling/Migrations/QuartzAdoJobStoreMigration.cs` — FM migration; creates the `quartz` schema + 11 `qrtz_*` tables per engine (via `Execute.Sql` of the canonical Quartz DDL).
- `tests/Themia.Modules.Scheduling.IntegrationTests/PersistentSchedulerTests.cs` — schema-exists + persistence-round-trip + opt-out tests.

**Modified:**
- `Directory.Packages.props` — pin `Quartz.Serialization.SystemTextJson` + `Quartz.Extensions.Hosting` (3.18.1).
- `src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj` — reference both.
- `src/modules/Themia.Modules.Scheduling/SchedulingModuleOptions.cs` — add `UsePersistentStore` (default `true`).
- `src/modules/Themia.Modules.Scheduling/SchedulingModule.cs` — register the persistent scheduler in `ConfigureServices`.
- `Directory.Build.props` (version), `CHANGELOG.md`, `MIGRATION.md`, roadmap docs.

---

## Task 1: Add the Quartz STJ-serializer + hosting packages

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj`

- [ ] **Step 1: Pin the two packages**

In `Directory.Packages.props`, next to the existing `<PackageVersion Include="Quartz" Version="3.18.1" />` line, add:

```xml
    <PackageVersion Include="Quartz.Serialization.SystemTextJson" Version="3.18.1" />
    <PackageVersion Include="Quartz.Extensions.Hosting" Version="3.18.1" />
```

- [ ] **Step 2: Reference them from the module**

In `src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj`, in the `PackageReference` ItemGroup (the one with `Microsoft.EntityFrameworkCore.SqlServer`), add:

```xml
    <PackageReference Include="Quartz" />
    <PackageReference Include="Quartz.Serialization.SystemTextJson" />
    <PackageReference Include="Quartz.Extensions.Hosting" />
```

(The module already gets `Quartz` transitively via the `Themia.Quartz` ProjectReference, but reference it directly now that the module calls `AddQuartz`/`UsePersistentStore` itself.)

- [ ] **Step 3: Restore + build to confirm the references resolve**

Run: `dotnet build src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj`
Expected: PASS, 0 warnings.

- [ ] **Step 4: Commit**

```bash
git add Directory.Packages.props src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj
git commit -m "chore: reference Quartz STJ serializer + hosting packages in the scheduling module"
```

---

## Task 2: `UsePersistentStore` option

**Files:**
- Modify: `src/modules/Themia.Modules.Scheduling/SchedulingModuleOptions.cs`
- Modify: `src/modules/Themia.Modules.Scheduling/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Add the option**

In `SchedulingModuleOptions.cs`, add inside the class (after `Authorize`):

```csharp
    /// <summary>
    /// When <see langword="true"/> (default), the module registers a persistent Quartz scheduler
    /// (AdoJobStore over the <c>quartz</c> schema, System.Text.Json serializer) and starts it via the
    /// Quartz hosted service. Set to <see langword="false"/> to register no scheduler — the host then
    /// supplies its own <c>IScheduler</c> (via <c>ThemiaQuartzOptions.Scheduler</c> or DI), as before.
    /// </summary>
    public bool UsePersistentStore { get; set; } = true;
```

- [ ] **Step 2: Record the public API**

In `src/modules/Themia.Modules.Scheduling/PublicAPI.Unshipped.txt`, add (alphabetical position near the other `SchedulingModuleOptions` lines):

```
Themia.Modules.Scheduling.SchedulingModuleOptions.UsePersistentStore.get -> bool
Themia.Modules.Scheduling.SchedulingModuleOptions.UsePersistentStore.set -> void
```

- [ ] **Step 3: Build (clean) to confirm PublicAPI is satisfied**

Run: `dotnet build src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj --no-incremental`
Expected: PASS, 0 warnings. If `RS0016` fires, copy the analyzer's exact suggested line into `PublicAPI.Unshipped.txt`.

- [ ] **Step 4: Commit**

```bash
git add src/modules/Themia.Modules.Scheduling/SchedulingModuleOptions.cs src/modules/Themia.Modules.Scheduling/PublicAPI.Unshipped.txt
git commit -m "feat: add SchedulingModuleOptions.UsePersistentStore (default on)"
```

---

## Task 3: The `qrtz_*` FluentMigrator migration

Ports Quartz 3.18.1's canonical DDL verbatim (schema-qualified to `quartz`) per engine via `Execute.Sql`, inside the LOCKSTEP `IfDatabase` pattern. `Execute.Sql` is used (not the FM fluent table builder) so the 11-table schema — composite PKs/FKs, `bytea`/`varbinary(max)` BLOBs, `bool`/`bit`, bigint epoch-millis — is a faithful copy of Quartz's own scripts, eliminating type-translation risk that would surface only as AdoJobStore runtime failures.

**Files:**
- Create: `src/modules/Themia.Modules.Scheduling/Migrations/QuartzAdoJobStoreMigration.cs`
- Modify: `src/modules/Themia.Modules.Scheduling/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Create the migration**

Create `src/modules/Themia.Modules.Scheduling/Migrations/QuartzAdoJobStoreMigration.cs`:

```csharp
using FluentMigrator;

namespace Themia.Modules.Scheduling.Migrations;

/// <summary>
/// Creates the Quartz AdoJobStore schema (the 11 <c>qrtz_*</c> tables) in a dedicated <c>quartz</c> schema,
/// for PostgreSQL and SQL Server. A faithful, schema-qualified port of Quartz.NET 3.18.1's canonical
/// <c>tables_postgres.sql</c> / <c>tables_sqlServer.sql</c> applied via <see cref="MigrationBase.Execute"/>
/// so the cross-engine BLOB/bool/bigint types and composite PK/FK constraints match Quartz exactly.
/// Greenfield (Themia owns the <c>quartz</c> schema exclusively) — FluentMigrator's VersionInfo provides
/// idempotency; no per-object existence guards.
/// </summary>
[Migration(202606130001, "Themia.Scheduling: create Quartz AdoJobStore (qrtz_*) schema")]
public sealed class QuartzAdoJobStoreMigration : Migration
{
    private const string SchemaName = "quartz";

    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP: this engine whitelist and the unsupported-provider guard below MUST cover the same set.
        // PostgreSQL + SQL Server only (no EF MySQL provider yet). Edit BOTH when adding an engine.
        IfDatabase("postgres").Delegate(() =>
        {
            Create.Schema(SchemaName);
            Execute.Sql(PostgresDdl);
        });

        IfDatabase("sqlserver").Delegate(() =>
        {
            Create.Schema(SchemaName);
            Execute.Sql(SqlServerDdl);
        });

        IfDatabase(p =>
                !p.StartsWith("Postgres", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", System.StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new System.NotSupportedException(
                "Themia.Scheduling persistent Quartz supports only PostgreSQL and SQL Server. The active " +
                "database provider is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        // Tables carry FKs; dropping the schema with CASCADE (PG) / dropping tables first (SQL Server)
        // is simplest. Down() runs only on explicit rollback, never in the MigrateUp startup path.
        IfDatabase("postgres").Delegate(() => Execute.Sql("DROP SCHEMA IF EXISTS quartz CASCADE;"));
        IfDatabase("sqlserver").Delegate(() => Execute.Sql(SqlServerDrop));
    }

    // --- Canonical Quartz 3.18.1 DDL, schema-qualified to `quartz`. Lowercase identifiers on PG ---
    private const string PostgresDdl = """
CREATE TABLE quartz.qrtz_job_details (
    sched_name TEXT NOT NULL,
    job_name TEXT NOT NULL,
    job_group TEXT NOT NULL,
    description TEXT NULL,
    job_class_name TEXT NOT NULL,
    is_durable BOOL NOT NULL,
    is_nonconcurrent BOOL NOT NULL,
    is_update_data BOOL NOT NULL,
    requests_recovery BOOL NOT NULL,
    job_data BYTEA NULL,
    PRIMARY KEY (sched_name, job_name, job_group)
);
CREATE TABLE quartz.qrtz_triggers (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    job_name TEXT NOT NULL,
    job_group TEXT NOT NULL,
    description TEXT NULL,
    next_fire_time BIGINT NULL,
    prev_fire_time BIGINT NULL,
    priority INTEGER NULL,
    trigger_state TEXT NOT NULL,
    trigger_type TEXT NOT NULL,
    start_time BIGINT NOT NULL,
    end_time BIGINT NULL,
    calendar_name TEXT NULL,
    misfire_instr SMALLINT NULL,
    misfire_orig_fire_time BIGINT NULL,
    execution_group VARCHAR(200) NULL,
    job_data BYTEA NULL,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, job_name, job_group)
      REFERENCES quartz.qrtz_job_details (sched_name, job_name, job_group)
);
CREATE TABLE quartz.qrtz_simple_triggers (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    repeat_count BIGINT NOT NULL,
    repeat_interval BIGINT NOT NULL,
    times_triggered BIGINT NOT NULL,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, trigger_name, trigger_group)
      REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);
CREATE TABLE quartz.qrtz_simprop_triggers (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    str_prop_1 TEXT NULL,
    str_prop_2 TEXT NULL,
    str_prop_3 TEXT NULL,
    int_prop_1 INTEGER NULL,
    int_prop_2 INTEGER NULL,
    long_prop_1 BIGINT NULL,
    long_prop_2 BIGINT NULL,
    dec_prop_1 NUMERIC NULL,
    dec_prop_2 NUMERIC NULL,
    bool_prop_1 BOOL NULL,
    bool_prop_2 BOOL NULL,
    time_zone_id TEXT NULL,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, trigger_name, trigger_group)
      REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);
CREATE TABLE quartz.qrtz_cron_triggers (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    cron_expression TEXT NOT NULL,
    time_zone_id TEXT,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, trigger_name, trigger_group)
      REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);
CREATE TABLE quartz.qrtz_blob_triggers (
    sched_name TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    blob_data BYTEA NULL,
    PRIMARY KEY (sched_name, trigger_name, trigger_group),
    FOREIGN KEY (sched_name, trigger_name, trigger_group)
      REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
);
CREATE TABLE quartz.qrtz_calendars (
    sched_name TEXT NOT NULL,
    calendar_name TEXT NOT NULL,
    calendar BYTEA NOT NULL,
    PRIMARY KEY (sched_name, calendar_name)
);
CREATE TABLE quartz.qrtz_paused_trigger_grps (
    sched_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    PRIMARY KEY (sched_name, trigger_group)
);
CREATE TABLE quartz.qrtz_fired_triggers (
    sched_name TEXT NOT NULL,
    entry_id TEXT NOT NULL,
    trigger_name TEXT NOT NULL,
    trigger_group TEXT NOT NULL,
    instance_name TEXT NOT NULL,
    fired_time BIGINT NOT NULL,
    sched_time BIGINT NOT NULL,
    priority INTEGER NOT NULL,
    state TEXT NOT NULL,
    job_name TEXT NULL,
    job_group TEXT NULL,
    is_nonconcurrent BOOL NOT NULL,
    requests_recovery BOOL NULL,
    execution_group VARCHAR(200) NULL,
    PRIMARY KEY (sched_name, entry_id)
);
CREATE TABLE quartz.qrtz_scheduler_state (
    sched_name TEXT NOT NULL,
    instance_name TEXT NOT NULL,
    last_checkin_time BIGINT NOT NULL,
    checkin_interval BIGINT NOT NULL,
    PRIMARY KEY (sched_name, instance_name)
);
CREATE TABLE quartz.qrtz_locks (
    sched_name TEXT NOT NULL,
    lock_name TEXT NOT NULL,
    PRIMARY KEY (sched_name, lock_name)
);
CREATE INDEX idx_qrtz_j_req_recovery ON quartz.qrtz_job_details (requests_recovery);
CREATE INDEX idx_qrtz_t_next_fire_time ON quartz.qrtz_triggers (next_fire_time);
CREATE INDEX idx_qrtz_t_state ON quartz.qrtz_triggers (trigger_state);
CREATE INDEX idx_qrtz_t_nft_st ON quartz.qrtz_triggers (next_fire_time, trigger_state);
CREATE INDEX idx_qrtz_ft_trig_name ON quartz.qrtz_fired_triggers (trigger_name);
CREATE INDEX idx_qrtz_ft_trig_group ON quartz.qrtz_fired_triggers (trigger_group);
CREATE INDEX idx_qrtz_ft_trig_nm_gp ON quartz.qrtz_fired_triggers (sched_name, trigger_name, trigger_group);
CREATE INDEX idx_qrtz_ft_trig_inst_name ON quartz.qrtz_fired_triggers (instance_name);
CREATE INDEX idx_qrtz_ft_job_name ON quartz.qrtz_fired_triggers (job_name);
CREATE INDEX idx_qrtz_ft_job_group ON quartz.qrtz_fired_triggers (job_group);
CREATE INDEX idx_qrtz_ft_job_req_recovery ON quartz.qrtz_fired_triggers (requests_recovery);
""";

    private const string SqlServerDdl = """
CREATE TABLE [quartz].[QRTZ_CALENDARS] ([SCHED_NAME] nvarchar(120) NOT NULL, [CALENDAR_NAME] nvarchar(200) NOT NULL, [CALENDAR] varbinary(max) NOT NULL);
CREATE TABLE [quartz].[QRTZ_CRON_TRIGGERS] ([SCHED_NAME] nvarchar(120) NOT NULL, [TRIGGER_NAME] nvarchar(150) NOT NULL, [TRIGGER_GROUP] nvarchar(150) NOT NULL, [CRON_EXPRESSION] nvarchar(120) NOT NULL, [TIME_ZONE_ID] nvarchar(80));
CREATE TABLE [quartz].[QRTZ_FIRED_TRIGGERS] ([SCHED_NAME] nvarchar(120) NOT NULL, [ENTRY_ID] nvarchar(140) NOT NULL, [TRIGGER_NAME] nvarchar(150) NOT NULL, [TRIGGER_GROUP] nvarchar(150) NOT NULL, [INSTANCE_NAME] nvarchar(200) NOT NULL, [FIRED_TIME] bigint NOT NULL, [SCHED_TIME] bigint NOT NULL, [PRIORITY] int NOT NULL, [STATE] nvarchar(16) NOT NULL, [JOB_NAME] nvarchar(150) NULL, [JOB_GROUP] nvarchar(150) NULL, [IS_NONCONCURRENT] bit NULL, [REQUESTS_RECOVERY] bit NULL, [EXECUTION_GROUP] nvarchar(200) NULL);
CREATE TABLE [quartz].[QRTZ_PAUSED_TRIGGER_GRPS] ([SCHED_NAME] nvarchar(120) NOT NULL, [TRIGGER_GROUP] nvarchar(150) NOT NULL);
CREATE TABLE [quartz].[QRTZ_SCHEDULER_STATE] ([SCHED_NAME] nvarchar(120) NOT NULL, [INSTANCE_NAME] nvarchar(200) NOT NULL, [LAST_CHECKIN_TIME] bigint NOT NULL, [CHECKIN_INTERVAL] bigint NOT NULL);
CREATE TABLE [quartz].[QRTZ_LOCKS] ([SCHED_NAME] nvarchar(120) NOT NULL, [LOCK_NAME] nvarchar(40) NOT NULL);
CREATE TABLE [quartz].[QRTZ_JOB_DETAILS] ([SCHED_NAME] nvarchar(120) NOT NULL, [JOB_NAME] nvarchar(150) NOT NULL, [JOB_GROUP] nvarchar(150) NOT NULL, [DESCRIPTION] nvarchar(250) NULL, [JOB_CLASS_NAME] nvarchar(250) NOT NULL, [IS_DURABLE] bit NOT NULL, [IS_NONCONCURRENT] bit NOT NULL, [IS_UPDATE_DATA] bit NOT NULL, [REQUESTS_RECOVERY] bit NOT NULL, [JOB_DATA] varbinary(max) NULL);
CREATE TABLE [quartz].[QRTZ_SIMPLE_TRIGGERS] ([SCHED_NAME] nvarchar(120) NOT NULL, [TRIGGER_NAME] nvarchar(150) NOT NULL, [TRIGGER_GROUP] nvarchar(150) NOT NULL, [REPEAT_COUNT] int NOT NULL, [REPEAT_INTERVAL] bigint NOT NULL, [TIMES_TRIGGERED] int NOT NULL);
CREATE TABLE [quartz].[QRTZ_SIMPROP_TRIGGERS] ([SCHED_NAME] nvarchar(120) NOT NULL, [TRIGGER_NAME] nvarchar(150) NOT NULL, [TRIGGER_GROUP] nvarchar(150) NOT NULL, [STR_PROP_1] nvarchar(512) NULL, [STR_PROP_2] nvarchar(512) NULL, [STR_PROP_3] nvarchar(512) NULL, [INT_PROP_1] int NULL, [INT_PROP_2] int NULL, [LONG_PROP_1] bigint NULL, [LONG_PROP_2] bigint NULL, [DEC_PROP_1] numeric(13,4) NULL, [DEC_PROP_2] numeric(13,4) NULL, [BOOL_PROP_1] bit NULL, [BOOL_PROP_2] bit NULL, [TIME_ZONE_ID] nvarchar(80) NULL);
CREATE TABLE [quartz].[QRTZ_BLOB_TRIGGERS] ([SCHED_NAME] nvarchar(120) NOT NULL, [TRIGGER_NAME] nvarchar(150) NOT NULL, [TRIGGER_GROUP] nvarchar(150) NOT NULL, [BLOB_DATA] varbinary(max) NULL);
CREATE TABLE [quartz].[QRTZ_TRIGGERS] ([SCHED_NAME] nvarchar(120) NOT NULL, [TRIGGER_NAME] nvarchar(150) NOT NULL, [TRIGGER_GROUP] nvarchar(150) NOT NULL, [JOB_NAME] nvarchar(150) NOT NULL, [JOB_GROUP] nvarchar(150) NOT NULL, [DESCRIPTION] nvarchar(250) NULL, [NEXT_FIRE_TIME] bigint NULL, [PREV_FIRE_TIME] bigint NULL, [PRIORITY] int NULL, [TRIGGER_STATE] nvarchar(16) NOT NULL, [TRIGGER_TYPE] nvarchar(8) NOT NULL, [START_TIME] bigint NOT NULL, [END_TIME] bigint NULL, [CALENDAR_NAME] nvarchar(200) NULL, [MISFIRE_INSTR] int NULL, [MISFIRE_ORIG_FIRE_TIME] bigint NULL, [EXECUTION_GROUP] nvarchar(200) NULL, [JOB_DATA] varbinary(max) NULL);
ALTER TABLE [quartz].[QRTZ_CALENDARS] ADD CONSTRAINT [PK_QRTZ_CALENDARS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [CALENDAR_NAME]);
ALTER TABLE [quartz].[QRTZ_CRON_TRIGGERS] ADD CONSTRAINT [PK_QRTZ_CRON_TRIGGERS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]);
ALTER TABLE [quartz].[QRTZ_FIRED_TRIGGERS] ADD CONSTRAINT [PK_QRTZ_FIRED_TRIGGERS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [ENTRY_ID]);
ALTER TABLE [quartz].[QRTZ_PAUSED_TRIGGER_GRPS] ADD CONSTRAINT [PK_QRTZ_PAUSED_TRIGGER_GRPS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [TRIGGER_GROUP]);
ALTER TABLE [quartz].[QRTZ_SCHEDULER_STATE] ADD CONSTRAINT [PK_QRTZ_SCHEDULER_STATE] PRIMARY KEY CLUSTERED ([SCHED_NAME], [INSTANCE_NAME]);
ALTER TABLE [quartz].[QRTZ_LOCKS] ADD CONSTRAINT [PK_QRTZ_LOCKS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [LOCK_NAME]);
ALTER TABLE [quartz].[QRTZ_JOB_DETAILS] ADD CONSTRAINT [PK_QRTZ_JOB_DETAILS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [JOB_NAME], [JOB_GROUP]);
ALTER TABLE [quartz].[QRTZ_SIMPLE_TRIGGERS] ADD CONSTRAINT [PK_QRTZ_SIMPLE_TRIGGERS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]);
ALTER TABLE [quartz].[QRTZ_SIMPROP_TRIGGERS] ADD CONSTRAINT [PK_QRTZ_SIMPROP_TRIGGERS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]);
ALTER TABLE [quartz].[QRTZ_TRIGGERS] ADD CONSTRAINT [PK_QRTZ_TRIGGERS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]);
ALTER TABLE [quartz].[QRTZ_BLOB_TRIGGERS] ADD CONSTRAINT [PK_QRTZ_BLOB_TRIGGERS] PRIMARY KEY CLUSTERED ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]);
ALTER TABLE [quartz].[QRTZ_CRON_TRIGGERS] ADD CONSTRAINT [FK_QRTZ_CRON_TRIGGERS_QRTZ_TRIGGERS] FOREIGN KEY ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]) REFERENCES [quartz].[QRTZ_TRIGGERS] ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]) ON DELETE CASCADE;
ALTER TABLE [quartz].[QRTZ_SIMPLE_TRIGGERS] ADD CONSTRAINT [FK_QRTZ_SIMPLE_TRIGGERS_QRTZ_TRIGGERS] FOREIGN KEY ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]) REFERENCES [quartz].[QRTZ_TRIGGERS] ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]) ON DELETE CASCADE;
ALTER TABLE [quartz].[QRTZ_SIMPROP_TRIGGERS] ADD CONSTRAINT [FK_QRTZ_SIMPROP_TRIGGERS_QRTZ_TRIGGERS] FOREIGN KEY ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]) REFERENCES [quartz].[QRTZ_TRIGGERS] ([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP]) ON DELETE CASCADE;
ALTER TABLE [quartz].[QRTZ_TRIGGERS] ADD CONSTRAINT [FK_QRTZ_TRIGGERS_QRTZ_JOB_DETAILS] FOREIGN KEY ([SCHED_NAME], [JOB_NAME], [JOB_GROUP]) REFERENCES [quartz].[QRTZ_JOB_DETAILS] ([SCHED_NAME], [JOB_NAME], [JOB_GROUP]);
CREATE INDEX [IDX_QRTZ_T_G_J] ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [JOB_GROUP], [JOB_NAME]);
CREATE INDEX [IDX_QRTZ_T_C] ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [CALENDAR_NAME]);
CREATE INDEX [IDX_QRTZ_T_N_G_STATE] ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [TRIGGER_GROUP], [TRIGGER_STATE]);
CREATE INDEX [IDX_QRTZ_T_STATE] ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [TRIGGER_STATE]);
CREATE INDEX [IDX_QRTZ_T_N_STATE] ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [TRIGGER_NAME], [TRIGGER_GROUP], [TRIGGER_STATE]);
CREATE INDEX [IDX_QRTZ_T_NEXT_FIRE_TIME] ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [NEXT_FIRE_TIME]);
CREATE INDEX [IDX_QRTZ_T_NFT_ST] ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [TRIGGER_STATE], [NEXT_FIRE_TIME]);
CREATE INDEX [IDX_QRTZ_T_NFT_ST_MISFIRE] ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [MISFIRE_INSTR], [NEXT_FIRE_TIME], [TRIGGER_STATE]);
CREATE INDEX [IDX_QRTZ_T_NFT_ST_MISFIRE_GRP] ON [quartz].[QRTZ_TRIGGERS]([SCHED_NAME], [MISFIRE_INSTR], [NEXT_FIRE_TIME], [TRIGGER_GROUP], [TRIGGER_STATE]);
CREATE INDEX [IDX_QRTZ_FT_INST_JOB_REQ_RCVRY] ON [quartz].[QRTZ_FIRED_TRIGGERS]([SCHED_NAME], [INSTANCE_NAME], [REQUESTS_RECOVERY]);
CREATE INDEX [IDX_QRTZ_FT_G_J] ON [quartz].[QRTZ_FIRED_TRIGGERS]([SCHED_NAME], [JOB_GROUP], [JOB_NAME]);
CREATE INDEX [IDX_QRTZ_FT_G_T] ON [quartz].[QRTZ_FIRED_TRIGGERS]([SCHED_NAME], [TRIGGER_GROUP], [TRIGGER_NAME]);
""";

    private const string SqlServerDrop = """
DROP TABLE IF EXISTS [quartz].[QRTZ_SIMPLE_TRIGGERS];
DROP TABLE IF EXISTS [quartz].[QRTZ_SIMPROP_TRIGGERS];
DROP TABLE IF EXISTS [quartz].[QRTZ_CRON_TRIGGERS];
DROP TABLE IF EXISTS [quartz].[QRTZ_BLOB_TRIGGERS];
DROP TABLE IF EXISTS [quartz].[QRTZ_TRIGGERS];
DROP TABLE IF EXISTS [quartz].[QRTZ_JOB_DETAILS];
DROP TABLE IF EXISTS [quartz].[QRTZ_CALENDARS];
DROP TABLE IF EXISTS [quartz].[QRTZ_PAUSED_TRIGGER_GRPS];
DROP TABLE IF EXISTS [quartz].[QRTZ_FIRED_TRIGGERS];
DROP TABLE IF EXISTS [quartz].[QRTZ_SCHEDULER_STATE];
DROP TABLE IF EXISTS [quartz].[QRTZ_LOCKS];
DROP SCHEMA IF EXISTS [quartz];
""";
}
```

- [ ] **Step 2: Record the public API**

In `src/modules/Themia.Modules.Scheduling/PublicAPI.Unshipped.txt`, add (matching how `SchedulingSchemaMigration` is tracked):

```
override Themia.Modules.Scheduling.Migrations.QuartzAdoJobStoreMigration.Down() -> void
override Themia.Modules.Scheduling.Migrations.QuartzAdoJobStoreMigration.Up() -> void
Themia.Modules.Scheduling.Migrations.QuartzAdoJobStoreMigration
Themia.Modules.Scheduling.Migrations.QuartzAdoJobStoreMigration.QuartzAdoJobStoreMigration() -> void
```

- [ ] **Step 3: Clean build (verify it compiles + PublicAPI is recorded)**

Run: `dotnet build src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj --no-incremental`
Expected: PASS, 0 warnings. If `RS0016` fires, reconcile `PublicAPI.Unshipped.txt` with the analyzer's exact lines. (Behavioral correctness is proven by the Task 5 integration tests, which apply this migration to real PG + SQL Server containers.)

- [ ] **Step 4: Commit**

```bash
git add src/modules/Themia.Modules.Scheduling/Migrations/QuartzAdoJobStoreMigration.cs src/modules/Themia.Modules.Scheduling/PublicAPI.Unshipped.txt
git commit -m "feat: add Quartz AdoJobStore (qrtz_*) FluentMigrator migration for the quartz schema"
```

---

## Task 4: Register the persistent scheduler (default-on, opt-out)

**Files:**
- Modify: `src/modules/Themia.Modules.Scheduling/SchedulingModule.cs`

- [ ] **Step 1: Add usings**

At the top of `SchedulingModule.cs`, add:

```csharp
using Quartz;
using Themia.Quartz.History;
```

(`Themia.Quartz.History` is the namespace of `ExecutionHistoryPlugin`; confirm via `grep -rn "class ExecutionHistoryPlugin" src/neutral/Themia.Quartz` and adjust the using to its actual namespace.)

- [ ] **Step 2: Register the scheduler at the end of `ConfigureServices`**

In `SchedulingModule.ConfigureServices`, after the existing `services.AddThemiaQuartz(...)` block, add:

```csharp
        if (options.UsePersistentStore)
        {
            services.AddQuartz(q =>
            {
                q.SchedulerName = options.SchedulerName;

                q.UsePersistentStore(s =>
                {
                    s.UseProperties = true;                 // JobDataMap stored as string key-values
                    s.UseSystemTextJsonSerializer();        // no Newtonsoft (CLAUDE.md)

                    // Engine + Default connection from the active IDatabaseProvider, qrtz_* in the `quartz` schema.
                    var sp = services.BuildServiceProvider();
                    var provider = sp.GetRequiredService<IDatabaseProvider>();
                    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
                        ?? throw new InvalidOperationException(
                            $"Connection string '{ConnectionStringName}' was not found; the scheduling module requires it.");

                    switch (provider.ProviderName)
                    {
                        case DatabaseProviderNames.Postgres:
                            s.UsePostgres(pg => { pg.ConnectionString = connectionString; pg.TablePrefix = "quartz.qrtz_"; });
                            break;
                        case DatabaseProviderNames.SqlServer:
                            s.UseSqlServer(ss => { ss.ConnectionString = connectionString; ss.TablePrefix = "quartz.qrtz_"; });
                            break;
                        default:
                            throw new NotSupportedException(
                                $"Themia.Scheduling persistent Quartz supports PostgreSQL and SQL Server; provider '{provider.ProviderName}' is not supported.");
                    }
                });

                // Themia owns the execution-history listener now (was added by the host).
                q.AddJobListener<ExecutionHistoryPlugin>(GroupMatcher<JobKey>.AnyGroup());
            });

            services.AddQuartzHostedService(h => h.WaitForJobsToComplete = true);
        }
```

> **API note for the implementer:** the Quartz 3.18 persistent-store config members used above — `UsePersistentStore`, `UseProperties`, `UseSystemTextJsonSerializer()`, `UsePostgres`/`UseSqlServer`, the `ConnectionString`/`TablePrefix` properties on the data-provider options, `AddJobListener`, and `AddQuartzHostedService` — are the documented 3.x names. If the build reports a missing member, check the exact signature against the referenced `Quartz`/`Quartz.Serialization.SystemTextJson`/`Quartz.Extensions.Hosting` 3.18.1 assemblies and adjust (e.g. `TablePrefix` may live on the persistent-store options `s` rather than the provider options — `s.TablePrefix = "quartz.qrtz_"`; the matcher type for `AddJobListener` is `GroupMatcher<JobKey>.AnyGroup()` or `EverythingMatcher<JobKey>.AllJobs()`). Do NOT change the schema/prefix/serializer intent — only reconcile member names. The avoid-`BuildServiceProvider`-in-ConfigureServices smell: if the engine/connection can be read without building a provider (e.g. the module already captured `IConfiguration` elsewhere, or via `q.UsePersistentStore` overload that takes an `IServiceProvider`), prefer that; otherwise the one-off `BuildServiceProvider()` here is acceptable at registration time (it mirrors how the DbContext factory resolves the provider per-build).

- [ ] **Step 3: Update the class XML doc**

In the `SchedulingModule` `<remarks>`, change the "host owns the Quartz IScheduler" paragraph to note that the module now registers a persistent AdoJobStore scheduler by default (the `quartz` schema, System.Text.Json), and `SchedulingModuleOptions.UsePersistentStore = false` restores the host-supplied path.

- [ ] **Step 4: Build**

Run: `dotnet build src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj`
Expected: PASS, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/modules/Themia.Modules.Scheduling/SchedulingModule.cs
git commit -m "feat: register default-on persistent Quartz scheduler (AdoJobStore) in the scheduling module"
```

---

## Task 5: Integration tests — schema, persistence round-trip, opt-out

**Files:**
- Create: `tests/Themia.Modules.Scheduling.IntegrationTests/PersistentSchedulerTests.cs`
- Reference: the existing `SchedulingModuleTestsBase` (engine-parameterized) + `FakeDatabaseProvider`.

- [ ] **Step 1: Add the qrtz-schema + persistence-round-trip tests to the base**

Add these to `SchedulingModuleTestsBase` (so they run on both `Postgres…` and `SqlServer…` derived classes). They build the module services (which now register the scheduler), run `InitializeAsync` (applies both migrations), then exercise persistence.

```csharp
    [Fact]
    public async Task InitializeAsync_CreatesQuartzAdoJobStoreSchema()
    {
        var provider = BuildModuleServices();
        var module = new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" });

        await module.InitializeAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        // qrtz_job_details exists in the quartz schema → COUNT succeeds (throws if absent).
        var count = await context.Database.SqlQueryRaw<int>(QrtzJobDetailsCountSql).ToListAsync();
        Assert.Equal(0, count[0]);
    }

    [Fact]
    public async Task PersistentScheduler_SurvivesRestart_ViaAdoJobStore()
    {
        // First "process": migrate, start a scheduler, schedule a durable job, shut down.
        var p1 = BuildModuleServices();
        await new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" }).InitializeAsync(p1);

        var factory1 = p1.GetRequiredService<ISchedulerFactory>();
        var scheduler1 = await factory1.GetScheduler();
        await scheduler1.Start();

        var jobKey = new JobKey("persisted-job", "persisted-group");
        var job = JobBuilder.Create<NoOpJob>().WithIdentity(jobKey).StoreDurably().Build();
        var trigger = TriggerBuilder.Create()
            .WithIdentity("persisted-trigger", "persisted-group")
            .ForJob(jobKey)
            .StartAt(DateTimeOffset.UtcNow.AddDays(1))
            .Build();
        await scheduler1.ScheduleJob(job, trigger);
        await scheduler1.Shutdown(waitForJobsToComplete: false);
        await p1.DisposeAsync();

        // Second "process": a fresh service provider over the SAME container DB; the job must still be there.
        var p2 = BuildModuleServices();
        var factory2 = p2.GetRequiredService<ISchedulerFactory>();
        var scheduler2 = await factory2.GetScheduler();
        await scheduler2.Start();

        Assert.True(await scheduler2.CheckExists(jobKey));
        var loaded = await scheduler2.GetTrigger(new TriggerKey("persisted-trigger", "persisted-group"));
        Assert.NotNull(loaded);

        await scheduler2.Shutdown(waitForJobsToComplete: false);
        await p2.DisposeAsync();
    }
```

Add the engine-specific `qrtz_job_details` count SQL as an **abstract member** alongside the 0.4.7 ones (each derived class implements it), and a `NoOpJob`:

In `SchedulingModuleTestsBase`:
```csharp
    protected abstract string QrtzJobDetailsCountSql { get; }
```
In `PostgresSchedulingModuleTests`:
```csharp
    protected override string QrtzJobDetailsCountSql =>
        "SELECT COUNT(*) AS \"Value\" FROM quartz.qrtz_job_details";
```
In `SqlServerSchedulingModuleTests`:
```csharp
    protected override string QrtzJobDetailsCountSql =>
        "SELECT COUNT(*) AS Value FROM quartz.qrtz_job_details";
```
A shared no-op job (new file `tests/Themia.Modules.Scheduling.IntegrationTests/NoOpJob.cs`):
```csharp
using Quartz;

namespace Themia.Modules.Scheduling.IntegrationTests;

[DisallowConcurrentExecution]
public sealed class NoOpJob : IJob
{
    public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
}
```

> Note: `BuildModuleServices()` already registers the module's services (including the scheduler now). `BuildServiceProvider` for the test must allow resolving `ISchedulerFactory` — Quartz registers it via `AddQuartz`. The `SchedulerName` "module-test" matches across both processes so the AdoJobStore rows align.

- [ ] **Step 2: Add the opt-out test (container-less is fine; put it in the existing `SchedulingModuleConfigurationTests`)**

In `SchedulingModuleConfigurationTests` (the container-less class):
```csharp
    [Fact]
    public void ConfigureServices_RegistersNoScheduler_WhenUsePersistentStoreFalse()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Host=unused" })
            .Build());
        services.AddSingleton<IDatabaseProvider>(new FakeDatabaseProvider(DatabaseProviderNames.Postgres));

        new SchedulingModule(new SchedulingModuleOptions { UsePersistentStore = false })
            .ConfigureServices(services);

        using var sp = services.BuildServiceProvider();
        Assert.Null(sp.GetService<ISchedulerFactory>());
    }
```
Add `using Quartz;` to the test file if not present.

- [ ] **Step 3: Run the integration tests (Docker; both engines)**

Run: `dotnet test tests/Themia.Modules.Scheduling.IntegrationTests/Themia.Modules.Scheduling.IntegrationTests.csproj`
Expected: PASS. The persistence round-trip pulls the mssql image on first run (minutes). If a test fails, diagnose the real cause (e.g. an AdoJobStore wiring mismatch — wrong table prefix/delegate, or a DDL type mismatch the migration introduced) — do NOT weaken assertions. A common first failure is the `tablePrefix` not matching the created tables: confirm the created `quartz.qrtz_*` names line up with the configured `quartz.qrtz_` prefix on both engines.

- [ ] **Step 4: Commit**

```bash
git add tests/Themia.Modules.Scheduling.IntegrationTests
git commit -m "test: persistent Quartz — qrtz_* schema + restart-survival round-trip (both engines) + opt-out"
```

---

## Task 6: Release wiring + roadmap reconcile

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `MIGRATION.md`, and the roadmap docs.

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>0.4.7</Version>` to `<Version>0.4.8</Version>`.

- [ ] **Step 2: CHANGELOG**

In `CHANGELOG.md`, below `## [Unreleased]`, insert:

```markdown

## 0.4.8 — 2026-06-12

### Added

- **Persistent Quartz (AdoJobStore), default-on.** `Themia.Modules.Scheduling` now registers and starts a
  Quartz.NET scheduler backed by AdoJobStore — the `qrtz_*` schema is created in a dedicated `quartz` schema by a
  FluentMigrator migration (PostgreSQL + SQL Server, run through `ThemiaMigrations.Run`), with
  `UseSystemTextJsonSerializer()` (no Newtonsoft) and `UseProperties = true`. Scheduled jobs now survive a
  restart. Set `SchedulingModuleOptions.UsePersistentStore = false` to keep a host-supplied scheduler.
```

- [ ] **Step 3: MIGRATION**

In `MIGRATION.md`, below the "How to read this guide" content (above `## 0.4.7`), insert:

```markdown
## 0.4.8

### Scheduling module now owns a persistent Quartz scheduler by default

**What changed:** `Themia.Modules.Scheduling` registers and starts a persistent AdoJobStore scheduler (the
`qrtz_*` tables in a `quartz` schema; System.Text.Json serializer; `UseProperties = true`). Previously the host
supplied the `IScheduler`.

**Why:** scheduled jobs must survive restarts; FluentMigrator owns the `qrtz_*` schema (DECISION #6).

**How to upgrade:**

- Ensure an EF provider is registered (`AddThemiaPostgres`/`AddThemiaSqlServer`) and call the module's
  `InitializeAsync` **before** running the host — the `qrtz_*` tables must exist before the scheduler starts.
- JobDataMap is stored as string key-values (`UseProperties = true`) — job data must be string-serializable.
- To keep managing your own scheduler, set `SchedulingModuleOptions.UsePersistentStore = false`; the module then
  registers no scheduler and the dashboard resolves your host-supplied `IScheduler` as before.
- The scheduler uses the `Default` connection (process-wide, never tenant-routed). SQL Server + PostgreSQL only.
```

- [ ] **Step 4: Reconcile the roadmap**

Update the 0.4.x roadmap in `docs/themia-architecture-overview.md`, `docs/superpowers/specs/2026-06-01-themia-release-strategy-design.md`, the 0.4.6 spec, and the 0.4.7 spec to: `… → 0.4.7 Scheduling EF→FM → 0.4.8 persistent Quartz (AdoJobStore + qrtz_* per-engine FM + System.Text.Json) → 0.4.9 raw-connection + DbSet.Find analyzer gate; EF MySQL deferred`. Read each file's current roadmap line first; edit only that line (surgical).

- [ ] **Step 5: Full clean build**

Run: `dotnet build Themia.sln --no-incremental`
Expected: PASS, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add Directory.Build.props CHANGELOG.md MIGRATION.md docs/
git commit -m "chore: release 0.4.8 — persistent Quartz; reconcile roadmap"
```

---

## Notes for the executor

- **No new package versions beyond the two Quartz packages** — `Npgsql`, `Microsoft.Data.SqlClient`, FluentMigrator, EF providers are already pinned.
- **`Execute.Sql` is deliberate** for the `qrtz_*` migration — it ports Quartz's canonical DDL verbatim (schema-qualified). Do not re-express it in the FM fluent table builder; a subtle type/constraint mismatch would only surface as an AdoJobStore runtime failure, which the round-trip test exists to catch but the literal port avoids.
- **SQL Server `CREATE SCHEMA` batch rule:** `Create.Schema("quartz")` is a separate FM command before the `Execute.Sql(SqlServerDdl)` table block, so the "CREATE SCHEMA must be first in its batch" rule is satisfied (the schema and tables are separate commands).
- **Run-ordering:** the host must call `module.InitializeAsync(sp)` before `app.Run()`; `AddQuartzHostedService` starts the scheduler during `app.Run()`, after the tables exist. The persistence test exercises this by starting the scheduler manually after `InitializeAsync`.
- The uncommitted **spec + this plan** (on `main`) should be committed on the feature branch at execution start.
- The full-solution build is green on `main` (0.4.7 merged); use targeted module builds for Tasks 1–4 and the full build for Task 6.
```
