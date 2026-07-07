using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Modules.Pdf.Migrations;

/// <summary>Creates the <c>pdf_templates</c> table (no schema) with per-tenant + global filtered unique
/// indexes on the resolution key, on PostgreSQL, MySQL, and SQL Server. FluentMigrator is the single DDL
/// authority for both the EF and Dapper data layers (DECISION #6).</summary>
[Migration(202607070001, "Themia.Pdf: create pdf_templates table")]
public sealed class PdfTemplateSchemaMigration : Migration
{
    /// <summary>Maps a datetime column to the engine-appropriate type. MySQL's FluentMigrator generator
    /// does not support <c>DateTimeOffset</c>, so MySQL uses <c>DATETIME(6)</c> while PostgreSQL and SQL
    /// Server use <c>datetimeoffset</c> (Postgres maps it to <c>timestamptz</c>), matching EF's
    /// DateTimeOffset mapping.</summary>
    private delegate ICreateTableColumnOptionOrWithColumnSyntax DateTimeType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql").Delegate(() => CreateTable(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTable(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTable(c => c.AsDateTimeOffset()));

        // Filtered unique indexes are not expressible via the fluent API, so they are emitted as raw SQL
        // with the reserved-word `key` column quoted per engine. The boolean literal differs per engine
        // (PostgreSQL: false, SQL Server bit: 0).
        IfDatabase("postgresql").Delegate(() => CreateFilteredUniqueIndexes("\"key\"", "false"));
        IfDatabase("sqlserver").Delegate(() => CreateFilteredUniqueIndexes("[key]", "0"));
        IfDatabase("mysql").Delegate(CreateMySqlUniqueIndexes);

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Modules.Pdf supports only PostgreSQL, MySQL, and SQL Server. The active " +
                "database provider is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("pdf_templates");

    private void CreateTable(DateTimeType dt)
    {
        var t = Create.Table("pdf_templates")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("key").AsString(200).NotNullable()
            .WithColumn("body").AsString(int.MaxValue).NotNullable()
            .WithColumn("name").AsString(400).Nullable()
            .WithColumn("description").AsString(int.MaxValue).Nullable();

        dt(t.WithColumn("created_at")).NotNullable();
        t.WithColumn("created_by").AsString(100).Nullable();
        dt(t.WithColumn("last_modified_at")).Nullable();
        t.WithColumn("last_modified_by").AsString(100).Nullable();
        t.WithColumn("is_deleted").AsBoolean().NotNullable().WithDefaultValue(false);
        dt(t.WithColumn("deleted_at")).Nullable();
        t.WithColumn("deleted_by").AsString(100).Nullable();
        dt(t.WithColumn("restored_at")).Nullable();
        t.WithColumn("restored_by").AsString(100).Nullable();
    }

    /// <summary>Emits the per-tenant and global filtered unique indexes on the resolution key for
    /// engines with native partial-index support (PostgreSQL, SQL Server), excluding soft-deleted rows
    /// so a deleted key can be re-created. <paramref name="keyColumn"/> is the quoted <c>key</c>
    /// identifier (<c>"key"</c> on PostgreSQL, <c>[key]</c> on SQL Server — <c>key</c> is a reserved
    /// word). <paramref name="falseLiteral"/> is the engine's boolean-false literal (<c>false</c> on
    /// PostgreSQL, <c>0</c> on SQL Server).</summary>
    private void CreateFilteredUniqueIndexes(string keyColumn, string falseLiteral)
    {
        Execute.Sql($"CREATE UNIQUE INDEX ux_pdf_templates_tenant_key ON pdf_templates (tenant_id, {keyColumn}) WHERE tenant_id IS NOT NULL AND is_deleted = {falseLiteral};");
        Execute.Sql($"CREATE UNIQUE INDEX ux_pdf_templates_global_key ON pdf_templates ({keyColumn}) WHERE tenant_id IS NULL AND is_deleted = {falseLiteral};");
    }

    /// <summary>MySQL lacks filtered/partial indexes, so the same per-tenant + global uniqueness is
    /// emulated with functional key parts that evaluate to <c>NULL</c> when the filter is false (MySQL
    /// treats each <c>NULL</c> as distinct in a unique index, so filtered-out rows do not collide).
    /// Soft-deleted rows are folded into the "filtered out" case so a deleted key can be re-created.
    /// Finalized/verified when the MySQL increment lands; this branch is not exercised in the PG increment.</summary>
    private void CreateMySqlUniqueIndexes()
    {
        Execute.Sql("CREATE UNIQUE INDEX ux_pdf_templates_tenant_key ON pdf_templates ((IF(tenant_id IS NULL OR is_deleted, NULL, tenant_id)), (IF(tenant_id IS NULL OR is_deleted, NULL, `key`)));");
        Execute.Sql("CREATE UNIQUE INDEX ux_pdf_templates_global_key ON pdf_templates ((IF(tenant_id IS NULL AND NOT is_deleted, `key`, NULL)));");
    }
}
