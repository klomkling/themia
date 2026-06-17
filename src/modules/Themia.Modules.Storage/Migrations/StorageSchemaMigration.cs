using System;
using FluentMigrator;

namespace Themia.Modules.Storage.Migrations;

/// <summary>Creates the <c>storage</c> schema and the <c>storage_objects</c> table with per-tenant +
/// platform filtered unique indexes on the logical key, on PostgreSQL and SQL Server.</summary>
[Migration(202606170001, "Themia.Storage: create storage schema and storage_objects")]
public sealed class StorageSchemaMigration : Migration
{
    private const string SchemaName = "storage";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql", "sqlserver").Delegate(CreateSchemaAndTable);
        // The boolean literal differs per engine (PostgreSQL: false, SQL Server bit: 0).
        IfDatabase("postgresql").Delegate(() => CreateFilteredIndexes(SchemaName, "false"));
        IfDatabase("sqlserver").Delegate(() => CreateFilteredIndexes($"[{SchemaName}]", "0"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Storage supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateSchemaAndTable()
    {
        if (!Schema.Schema(SchemaName).Exists())
        {
            Create.Schema(SchemaName);
        }

        Create.Table("storage_objects").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("key").AsString(1024).NotNullable()
            .WithColumn("content_type").AsString(256).NotNullable()
            .WithColumn("size_bytes").AsInt64().NotNullable()
            .WithColumn("etag").AsString(256).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable()
            .WithColumn("is_deleted").AsBoolean().NotNullable()
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_by").AsString(100).Nullable()
            .WithColumn("restored_at").AsDateTimeOffset().Nullable()
            .WithColumn("restored_by").AsString(100).Nullable();

        // Quota scan path: usage is summed per tenant.
        Create.Index("ix_storage_objects_tenant").OnTable("storage_objects").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending();
    }

    /// <summary>Emits the per-tenant + platform filtered unique indexes on the logical key, excluding
    /// soft-deleted rows so a deleted key can be re-uploaded. <paramref name="schema"/> is pre-escaped
    /// (<c>storage</c> on PostgreSQL, <c>[storage]</c> on SQL Server); <paramref name="falseLiteral"/> is
    /// the engine's boolean-false literal (<c>false</c> on PostgreSQL, <c>0</c> on SQL Server).</summary>
    private void CreateFilteredIndexes(string schema, string falseLiteral)
    {
        Execute.Sql($"CREATE UNIQUE INDEX ux_storage_objects_tenant_key ON {schema}.storage_objects (tenant_id, key) WHERE tenant_id IS NOT NULL AND is_deleted = {falseLiteral};");
        Execute.Sql($"CREATE UNIQUE INDEX ux_storage_objects_platform_key ON {schema}.storage_objects (key) WHERE tenant_id IS NULL AND is_deleted = {falseLiteral};");
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("storage_objects").InSchema(SchemaName);
}
