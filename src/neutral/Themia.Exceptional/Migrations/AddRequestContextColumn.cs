using FluentMigrator;

namespace Themia.Exceptional.Migrations;

/// <summary>Adds the nullable <c>RequestContext</c> column (JSON request context) to <c>Exceptions</c>.
/// Additive + nullable, so it is safe on existing rows and existing engines.</summary>
[Migration(202606220001, "Themia.Exceptional: add RequestContext column")]
public sealed class AddRequestContextColumn : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP with ExceptionLogMigration's provider whitelist (PostgreSQL/MySQL/SqlServer).
        IfDatabase("postgresql", "mysql", "sqlserver")
            .Alter.Table("Exceptions")
            .AddColumn("RequestContext").AsString(int.MaxValue).Nullable();

        IfDatabase(p =>
                !p.StartsWith("Postgres", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", System.StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new System.NotSupportedException(
                "Themia.Exceptional supports only PostgreSQL, MySQL/MariaDB, and SQL Server."));
    }

    /// <inheritdoc />
    public override void Down() => Delete.Column("RequestContext").FromTable("Exceptions");
}
