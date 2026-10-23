using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Adds <c>identity.roles.level</c>, the role's rank 1 (highest) to 5, defaulting to 5 so existing roles
/// become the lowest level until the app sets them. Forward-only addition after the refresh-token schema.
/// PostgreSQL and SQL Server only.</summary>
[Migration(202610230001, "Themia.Identity: add roles.level smallint (1..5, default 5)")]
public sealed class AddRoleLevelMigration : Migration
{
    private const string SchemaName = "identity";
    private const string CheckName = "ck_roles_level";

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe (coord #0078): adopt a column that is already there.
        if (Schema.Schema(SchemaName).Table("roles").Column("level").Exists())
        {
            return;
        }

        IfDatabase("postgresql", "sqlserver").Delegate(AddColumn);
        // 'identity' is a reserved keyword in SQL Server — the schema qualifier must be bracketed.
        IfDatabase("postgresql").Delegate(() => AddCheck(SchemaName));
        IfDatabase("sqlserver").Delegate(() => AddCheck($"[{SchemaName}]"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void AddColumn() =>
        Alter.Table("roles").InSchema(SchemaName).AddColumn("level").AsInt16().NotNullable().WithDefaultValue(5);

    private void AddCheck(string schema) =>
        Execute.Sql($"ALTER TABLE {schema}.roles ADD CONSTRAINT {CheckName} CHECK (level BETWEEN 1 AND 5);");

    /// <inheritdoc />
    public override void Down()
    {
        IfDatabase("postgresql").Delegate(() => DropCheck(SchemaName));
        IfDatabase("sqlserver").Delegate(() => DropCheck($"[{SchemaName}]"));
        Delete.Column("level").FromTable("roles").InSchema(SchemaName);
    }

    private void DropCheck(string schema) =>
        Execute.Sql($"ALTER TABLE {schema}.roles DROP CONSTRAINT {CheckName};");
}
