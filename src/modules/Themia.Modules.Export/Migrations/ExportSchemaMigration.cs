using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Modules.Export.Migrations;

/// <summary>Creates the <c>export</c> schema and its two tables (<c>export_runs</c>, <c>export_schedules</c>)
/// on PostgreSQL and SQL Server. FluentMigrator is the single DDL authority for both the EF and Dapper data
/// layers (DECISION #6).</summary>
[Migration(202606270001, "Themia.Export: create export schema and tables")]
public sealed class ExportSchemaMigration : Migration
{
    private const string SchemaName = "export";

    /// <summary>Maps a datetime column to the engine-appropriate type. Both supported engines use
    /// <c>datetimeoffset</c> (Postgres maps it to <c>timestamptz</c>), matching EF's DateTimeOffset mapping.</summary>
    private delegate ICreateTableColumnOptionOrWithColumnSyntax DateTimeType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP: this engine whitelist and the unsupported-provider guard below must agree. PostgreSQL
        // and SQL Server are the only engines with an EF provider today (no EF MySQL — Pomelo has no EF
        // Core 10 build), and the runtime provider switch in ExportModuleServiceCollectionExtensions only
        // accepts those two. When EF MySQL lands, add a branch here AND to the guard AND to the runtime switch.
        IfDatabase("postgresql", "sqlserver").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Modules.Export supports only PostgreSQL and SQL Server. The active " +
                "database provider is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("export_schedules").InSchema(SchemaName);
        Delete.Table("export_runs").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }

    private void CreateTables(DateTimeType dt)
    {
        if (!Schema.Schema(SchemaName).Exists())
        {
            Create.Schema(SchemaName);
        }

        CreateRuns(dt);
        CreateSchedules(dt);
    }

    private void CreateRuns(DateTimeType dt)
    {
        var runs = Create.Table("export_runs").InSchema(SchemaName)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsString(100).Nullable()
            .WithColumn("definition_key").AsString(200).NotNullable()
            .WithColumn("parameters_json").AsString(int.MaxValue).Nullable()
            .WithColumn("format").AsInt32().NotNullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("include_soft_deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("storage_key").AsString(400).Nullable()
            .WithColumn("file_name").AsString(260).Nullable()
            .WithColumn("size_bytes").AsInt64().Nullable();
        dt(runs.WithColumn("started_at")).Nullable();
        dt(runs.WithColumn("expires_at")).Nullable();
        runs.WithColumn("error").AsString(int.MaxValue).Nullable();
        dt(runs.WithColumn("completed_at")).Nullable();
        dt(runs.WithColumn("created_at")).NotNullable();
        runs.WithColumn("created_by").AsString(100).Nullable();
        dt(runs.WithColumn("last_modified_at")).Nullable();
        runs.WithColumn("last_modified_by").AsString(100).Nullable();
        runs.WithColumn("is_deleted").AsBoolean().NotNullable().WithDefaultValue(false);
        dt(runs.WithColumn("deleted_at")).Nullable();
        runs.WithColumn("deleted_by").AsString(100).Nullable();
        dt(runs.WithColumn("restored_at")).Nullable();
        runs.WithColumn("restored_by").AsString(100).Nullable();

        Create.Index("ix_export_runs_tenant_status").OnTable("export_runs").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending().OnColumn("status").Ascending();
        Create.Index("ix_export_runs_expires_at").OnTable("export_runs").InSchema(SchemaName)
            .OnColumn("expires_at").Ascending();
    }

    private void CreateSchedules(DateTimeType dt)
    {
        var schedules = Create.Table("export_schedules").InSchema(SchemaName)
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsString(100).Nullable()
            .WithColumn("definition_key").AsString(200).NotNullable()
            .WithColumn("parameters_json").AsString(int.MaxValue).Nullable()
            .WithColumn("format").AsInt32().NotNullable()
            .WithColumn("cron").AsString(120).NotNullable()
            .WithColumn("enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("include_soft_deleted").AsBoolean().NotNullable().WithDefaultValue(false);
        dt(schedules.WithColumn("created_at")).NotNullable();
        schedules.WithColumn("created_by").AsString(100).Nullable();
        dt(schedules.WithColumn("last_modified_at")).Nullable();
        schedules.WithColumn("last_modified_by").AsString(100).Nullable();
        schedules.WithColumn("is_deleted").AsBoolean().NotNullable().WithDefaultValue(false);
        dt(schedules.WithColumn("deleted_at")).Nullable();
        schedules.WithColumn("deleted_by").AsString(100).Nullable();
        dt(schedules.WithColumn("restored_at")).Nullable();
        schedules.WithColumn("restored_by").AsString(100).Nullable();

        Create.Index("ix_export_schedules_tenant_enabled").OnTable("export_schedules").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending().OnColumn("enabled").Ascending();
    }
}
