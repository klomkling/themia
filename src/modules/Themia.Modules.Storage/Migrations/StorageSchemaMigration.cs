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
        IfDatabase("postgresql").Delegate(() => CreateFilteredIndexes(SchemaName, "\"key\"", "false"));
        IfDatabase("sqlserver").Delegate(() => CreateFilteredIndexes($"[{SchemaName}]", "[key]", "0"));


        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Storage supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private const string TenantIndexName = "ix_storage_objects_tenant";

    // Replay-safe, per OBJECT rather than per schema. The per-assembly version ledger (coord #0078)
    // starts empty on every existing database, so this Up() runs once against objects that are already
    // there — guarding only the schema would then leave CREATE TABLE to fail with 42P07 and crash the
    // host at boot. Existence is captured before any CREATE so the checks read the pre-migration state
    // and never depend on statement order within Up().
    private void CreateSchemaAndTable()
    {
        var schemaExists = Schema.Schema(SchemaName).Exists();
        var tableExists = Schema.Schema(SchemaName).Table("storage_objects").Exists();
        var tenantIndexExists = tableExists
            && Schema.Schema(SchemaName).Table("storage_objects").Index(TenantIndexName).Exists();

        if (!schemaExists)
        {
            Create.Schema(SchemaName);
        }

        if (tableExists)
        {
            if (!tenantIndexExists)
            {
                CreateTenantIndex();
            }

            return;
        }

        Create.Table("storage_objects").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("key").AsString(1024).NotNullable()
            .WithColumn("content_type").AsString(256).NotNullable()
            .WithColumn("size_bytes").AsInt64().NotNullable()
            .WithColumn("etag").AsString(256).Nullable()
            .WithColumn("committed_at").AsDateTimeOffset().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable()
            .WithColumn("is_deleted").AsBoolean().NotNullable()
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_by").AsString(100).Nullable()
            .WithColumn("restored_at").AsDateTimeOffset().Nullable()
            .WithColumn("restored_by").AsString(100).Nullable();

        CreateTenantIndex();
    }

    /// <summary>Quota scan path: usage is summed per tenant.</summary>
    private void CreateTenantIndex() =>
        Create.Index(TenantIndexName).OnTable("storage_objects").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending();

    /// <summary>Emits the per-tenant + platform filtered unique indexes on the logical key, excluding
    /// soft-deleted rows so a deleted key can be re-uploaded. <paramref name="schema"/> is pre-escaped
    /// (<c>storage</c> on PostgreSQL, <c>[storage]</c> on SQL Server); <paramref name="keyColumn"/> is the
    /// quoted <c>key</c> identifier (<c>"key"</c> on PostgreSQL, <c>[key]</c> on SQL Server — <c>key</c> is a
    /// reserved word on SQL Server); <paramref name="falseLiteral"/> is the engine's boolean-false literal
    /// (<c>false</c> on PostgreSQL, <c>0</c> on SQL Server).</summary>
    private void CreateFilteredIndexes(string schema, string keyColumn, string falseLiteral)
    {
        // Guarded for the same reason as the table above: a replay against an existing database reaches
        // here too. These are raw SQL because neither engine expresses a filtered unique index through
        // FluentMigrator's builder, so the existence check has to be made explicitly rather than by IF
        // NOT EXISTS — SQL Server has no such clause for CREATE INDEX.
        if (!Schema.Schema(SchemaName).Table("storage_objects").Index("ux_storage_objects_tenant_key").Exists())
        {
            Execute.Sql($"CREATE UNIQUE INDEX ux_storage_objects_tenant_key ON {schema}.storage_objects (tenant_id, {keyColumn}) WHERE tenant_id IS NOT NULL AND is_deleted = {falseLiteral};");
        }

        if (!Schema.Schema(SchemaName).Table("storage_objects").Index("ux_storage_objects_platform_key").Exists())
        {
            Execute.Sql($"CREATE UNIQUE INDEX ux_storage_objects_platform_key ON {schema}.storage_objects ({keyColumn}) WHERE tenant_id IS NULL AND is_deleted = {falseLiteral};");
        }
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("storage_objects").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }
}
