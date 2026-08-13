using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.AspNetCore.DataProtection.Migrations;

/// <summary>
/// Creates the <c>data_protection_keys</c> table. The XML column type is rendered per provider.
/// </summary>
[Migration(202607260001, "Themia.AspNetCore.DataProtection: create data_protection_keys table")]
public sealed class DataProtectionKeysMigration : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe: the ledger this migration records itself in moved from FluentMigrator's shared
        // VersionInfo to a Themia-owned table (coord #0078), so on an existing database every Themia
        // migration is replayed once. Without this guard that replay is a CREATE TABLE against a table
        // that already exists — a boot failure. With it, a replay adopts what is there.
        //
        // It also self-heals the defect that prompted the change: where a consumer migration carrying the
        // same version number silently skipped this one, the table genuinely does not exist and the
        // replay creates it.
        if (Schema.Table("data_protection_keys").Exists())
        {
            return;
        }

        // LOCKSTEP: this per-provider list and the unsupported-provider guard below are two parallel
        // whitelists that MUST agree. Adding a provider here without adding its prefix to the guard leaves it
        // throwing NotSupportedException; adding it to the guard without a branch here lets it through to a
        // column-type failure. Edit BOTH when adding a provider.
        //
        // A key element is a few hundred bytes, but the column is unbounded on every engine: the payload is
        // opaque to us and grows with the algorithm and any key-encryption wrapper the application configures.
        IfDatabase("postgresql").Delegate(() => CreateTable(c => c.AsCustom("TEXT")));
        IfDatabase("mysql").Delegate(() => CreateTable(c => c.AsCustom("LONGTEXT")));
        IfDatabase("sqlserver").Delegate(() => CreateTable(c => c.AsCustom("NVARCHAR(MAX)")));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.AspNetCore.DataProtection supports only PostgreSQL, MySQL, and SQL Server. " +
                "The active database provider is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("data_protection_keys");

    private void CreateTable(Func<ICreateTableColumnAsTypeSyntax, ICreateTableColumnOptionOrWithColumnSyntax> xmlType)
    {
        var table = Create.Table("data_protection_keys")
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("friendly_name").AsString(512).Nullable();

        // created_at carries no behaviour — the key ring's own lifetime metadata lives inside the XML. It is
        // here for operators, so it is set from the server clock at insert (see IDataProtectionKeyDialect)
        // rather than from whichever application server happened to write the key.
        xmlType(table.WithColumn("xml")).NotNullable()
            .WithColumn("created_at").AsDateTime().NotNullable();
    }
}
