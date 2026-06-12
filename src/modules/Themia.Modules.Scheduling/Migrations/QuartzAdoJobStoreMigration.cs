using FluentMigrator;

namespace Themia.Modules.Scheduling.Migrations;

/// <summary>
/// Creates the Quartz AdoJobStore schema (the 11 <c>qrtz_*</c> tables) in a dedicated <c>quartz</c> schema,
/// for PostgreSQL and SQL Server. A faithful, schema-qualified port of Quartz.NET 3.18.1's canonical
/// <c>tables_postgres.sql</c> / <c>tables_sqlServer.sql</c> applied via <c>Execute.Sql</c>
/// so the cross-engine BLOB/bool/bigint types and composite PK/FK constraints match Quartz exactly.
/// Themia owns the <c>quartz</c> schema exclusively. FluentMigrator's VersionInfo normally provides
/// idempotency, but the EF→FM cutover path can present a database whose objects already exist with no
/// VersionInfo row (a pre-0.4.7 deployment), forcing a replay; the schema/table existence guards below
/// keep that replay safe instead of failing on a duplicate <c>CREATE SCHEMA</c>/<c>CREATE TABLE</c>.
/// </summary>
[Migration(202606130001, "Themia.Scheduling: create Quartz AdoJobStore (qrtz_*) schema")]
public sealed class QuartzAdoJobStoreMigration : Migration
{
    // Used by the fluent schema guards/creation in CreateSchemaAndTables. The verbatim Quartz DDL constants
    // and SqlServerDrop carry this name literally (for byte-for-byte fidelity with Quartz's canonical SQL),
    // so changing it here must be mirrored in those SQL strings — it is not a standalone knob.
    private const string SchemaName = "quartz";

    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP: this engine whitelist and the unsupported-provider guard below MUST cover the same set.
        // PostgreSQL + SQL Server only (no EF MySQL provider yet). Edit BOTH when adding an engine.
        IfDatabase("postgres").Delegate(() => CreateSchemaAndTables(PostgresDdl));
        IfDatabase("sqlserver").Delegate(() => CreateSchemaAndTables(SqlServerDdl));

        IfDatabase(p =>
                !p.StartsWith("Postgres", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", System.StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new System.NotSupportedException(
                "Themia.Scheduling persistent Quartz supports only PostgreSQL and SQL Server. The active " +
                "database provider is not supported; add a migration branch for it."));
    }

    // Creates the quartz schema and its qrtz_* tables, each guarded so a VersionInfo-less replay (the EF→FM
    // cutover path) adopts existing objects instead of failing on a duplicate CREATE. qrtz_job_details is the
    // root table of the qrtz_* graph; if it exists the whole canonical DDL block has already been applied.
    // This guard is intentionally all-or-nothing (one root-table check gates the entire block), unlike the
    // sibling SchedulingSchemaMigration's per-object guards: the qrtz_* DDL is one atomic Execute.Sql block,
    // so a partial schema cannot arise from a failed replay (only from manual intervention) and need not be
    // repaired object-by-object.
    private void CreateSchemaAndTables(string ddl)
    {
        if (!Schema.Schema(SchemaName).Exists())
        {
            Create.Schema(SchemaName);
        }

        if (!Schema.Schema(SchemaName).Table("qrtz_job_details").Exists())
        {
            Execute.Sql(ddl);
        }
    }

    /// <inheritdoc />
    public override void Down()
    {
        // Tables carry FKs; dropping the schema with CASCADE (PG) / dropping tables first (SQL Server)
        // is simplest. Down() runs only on explicit rollback, never in the MigrateUp startup path.
        IfDatabase("postgres").Delegate(() => Execute.Sql($"DROP SCHEMA IF EXISTS {SchemaName} CASCADE;"));
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
