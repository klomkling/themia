using FluentMigrator;

namespace Themia.AspNetCore.DataProtection.Migrations;

/// <summary>
/// Gives <c>data_protection_keys.created_at</c> a server-clock UTC default.
/// </summary>
/// <remarks>
/// coord #0096. The column has always been <c>NOT NULL</c> with no default, which is invisible to Themia —
/// <see cref="IDataProtectionKeyDialect.InsertSql"/> always supplies the value explicitly — and visible to
/// anyone else. A consumer who created this table before adopting the module may well have declared a
/// default (the reported one did), and the guard in
/// <see cref="DataProtectionKeysMigration"/> adopts either shape without comment. So whether an INSERT that
/// omits <c>created_at</c> succeeds or fails depends on which of two histories a database happened to have,
/// and nothing records which.
/// <para>
/// A separate migration rather than an edit to <see cref="DataProtectionKeysMigration"/>: that one is
/// deployed, and deployed migrations are not edited. Fresh databases run both in order and end in the same
/// state as upgraded ones, which is the point.
/// </para>
/// <para>
/// <b>The default is UTC on every engine</b>, matching what each dialect already writes —
/// <c>now() AT TIME ZONE 'utc'</c>, <c>UTC_TIMESTAMP(6)</c>, <c>SYSUTCDATETIME()</c>. A local-clock default
/// such as PostgreSQL's bare <c>NOW()</c> would put local timestamps in a column whose existing rows are
/// UTC, which is worse than having no default at all: the rows are indistinguishable afterwards.
/// </para>
/// <para>
/// Replay-safe, like everything else here (coord #0096): PostgreSQL's <c>SET DEFAULT</c> and MySQL's
/// <c>MODIFY COLUMN</c> are idempotent by nature, and SQL Server's default is a named constraint, so it is
/// guarded explicitly.
/// </para>
/// </remarks>
[Migration(202608220001, "Themia.AspNetCore.DataProtection: default data_protection_keys.created_at to UTC now")]
public sealed class DataProtectionKeysCreatedAtDefaultMigration : Migration
{
    private const string SqlServerConstraintName = "df_data_protection_keys_created_at";

    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP with the guard below and with DataProtectionKeysMigration's provider list: three
        // parallel whitelists that must agree. Adding a provider to one without the others leaves it
        // either throwing or silently unhandled.
        IfDatabase("postgresql").Delegate(() => Execute.Sql(
            "ALTER TABLE data_protection_keys ALTER COLUMN created_at SET DEFAULT (now() AT TIME ZONE 'utc');"));

        // MySQL restates the column type on MODIFY, so it must match what DataProtectionKeysMigration
        // created (AsDateTime() -> DATETIME). The parenthesised expression default needs 8.0.13+, which is
        // already Themia's floor. UTC_TIMESTAMP() rather than (6): the column has no fractional precision,
        // so asking for it would only be rounded away.
        IfDatabase("mysql").Delegate(() => Execute.Sql(
            "ALTER TABLE data_protection_keys MODIFY COLUMN created_at DATETIME NOT NULL "
            + "DEFAULT (UTC_TIMESTAMP());"));

        // SQL Server has no "set default" — a default is a constraint, so a replay would fail on the name
        // rather than no-op. Hence the explicit check.
        IfDatabase("sqlserver").Delegate(() => Execute.Sql(
            "IF NOT EXISTS (SELECT 1 FROM sys.default_constraints "
            + "WHERE parent_object_id = OBJECT_ID('data_protection_keys') "
            + "AND COL_NAME(parent_object_id, parent_column_id) = 'created_at') "
            + $"ALTER TABLE [data_protection_keys] ADD CONSTRAINT [{SqlServerConstraintName}] "
            + "DEFAULT SYSUTCDATETIME() FOR [created_at];"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.AspNetCore.DataProtection supports only PostgreSQL, MySQL, and SQL Server. " +
                "The active database provider is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        IfDatabase("postgresql").Delegate(() => Execute.Sql(
            "ALTER TABLE data_protection_keys ALTER COLUMN created_at DROP DEFAULT;"));

        IfDatabase("mysql").Delegate(() => Execute.Sql(
            "ALTER TABLE data_protection_keys MODIFY COLUMN created_at DATETIME NOT NULL;"));

        IfDatabase("sqlserver").Delegate(() => Execute.Sql(
            $"IF OBJECT_ID('{SqlServerConstraintName}', 'D') IS NOT NULL "
            + $"ALTER TABLE [data_protection_keys] DROP CONSTRAINT [{SqlServerConstraintName}];"));
    }
}
