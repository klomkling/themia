using FluentMigrator;

namespace Themia.Modules.Scheduling.Migrations;

/// <summary>
/// Creates the <c>scheduling</c> schema and its two tables (<c>execution_history</c>,
/// <c>scheduler_stats</c>). Replaces the former EF Core migration — FluentMigrator is the single
/// schema authority (DECISION #6). Run through <c>ThemiaMigrations.Run</c> at module startup.
/// </summary>
[Migration(202606120001, "Themia.Scheduling: create scheduling schema and tables")]
public sealed class SchedulingSchemaMigration : Migration
{
    private const string SchemaName = "scheduling";

    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP: this engine whitelist and the unsupported-provider guard below are two parallel
        // whitelists that MUST agree. PostgreSQL and SQL Server are the only engines with an EF provider
        // today (no EF MySQL — Pomelo has no EF Core 10 build). FluentMigrator's AsDateTimeOffset maps to
        // timestamptz (Postgres) / datetimeoffset (SQL Server), matching EF's DateTimeOffset mapping, so a
        // single CreateTable serves both. When EF MySQL lands, add a branch here AND to the guard.
        IfDatabase("postgres", "sqlserver").Delegate(CreateSchemaAndTables);

        IfDatabase(p =>
                !p.StartsWith("Postgres", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", System.StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new System.NotSupportedException(
                "Themia.Scheduling supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    // Idempotent against a pre-existing scheduling schema. A database deployed before 0.4.7 already carries
    // these tables (created by the old EF migration) but has no FluentMigrator VersionInfo row, so this Up()
    // runs on the first FM execution. The existence guards let it adopt the existing objects — and record the
    // version — instead of failing on CREATE. On a fresh database every guard is false and all objects are
    // created; on subsequent runs VersionInfo skips this migration entirely.
    private void CreateSchemaAndTables()
    {
        if (!Schema.Schema(SchemaName).Exists())
            Create.Schema(SchemaName);

        if (!Schema.Schema(SchemaName).Table("execution_history").Exists())
        {
            Create.Table("execution_history").InSchema(SchemaName)
                .WithColumn("fire_instance_id").AsString(256).NotNullable().PrimaryKey()
                .WithColumn("scheduler_instance_id").AsString(256).Nullable()
                .WithColumn("scheduler_name").AsString(256).Nullable()
                .WithColumn("job").AsString(512).Nullable()
                .WithColumn("trigger").AsString(512).Nullable()
                .WithColumn("scheduled_fire_time_utc").AsDateTimeOffset().Nullable()
                .WithColumn("actual_fire_time_utc").AsDateTimeOffset().NotNullable()
                .WithColumn("recovering").AsBoolean().NotNullable()
                .WithColumn("vetoed").AsBoolean().NotNullable()
                .WithColumn("finished_time_utc").AsDateTimeOffset().Nullable()
                .WithColumn("exception_message").AsString(4000).Nullable();

            Create.Index("ix_execution_history_scheduler_trigger_fired")
                .OnTable("execution_history").InSchema(SchemaName)
                .OnColumn("scheduler_name").Ascending()
                .OnColumn("trigger").Ascending()
                .OnColumn("actual_fire_time_utc").Ascending();
        }

        if (!Schema.Schema(SchemaName).Table("scheduler_stats").Exists())
        {
            Create.Table("scheduler_stats").InSchema(SchemaName)
                .WithColumn("scheduler_name").AsString(256).NotNullable().PrimaryKey()
                .WithColumn("total_jobs_executed").AsInt32().NotNullable()
                .WithColumn("total_jobs_failed").AsInt32().NotNullable();
        }
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("scheduler_stats").InSchema(SchemaName);
        Delete.Table("execution_history").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }
}
