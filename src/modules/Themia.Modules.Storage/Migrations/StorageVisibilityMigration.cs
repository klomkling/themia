using System;
using FluentMigrator;

namespace Themia.Modules.Storage.Migrations;

/// <summary>Adds <c>storage_objects.visibility</c>. Every pre-existing object is private — that is the
/// correct default and the reason private physical keys were left unprefixed: no stored blob has to move.</summary>
[Migration(202607150001, "Themia.Storage: add storage_objects.visibility")]
public sealed class StorageVisibilityMigration : Migration
{
    private const string SchemaName = "storage";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql", "sqlserver").Delegate(() =>
            Create.Column("visibility").OnTable("storage_objects").InSchema(SchemaName)
                .AsInt32().NotNullable().WithDefaultValue(0)); // 0 = StorageVisibility.Private

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Storage supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Column("visibility").FromTable("storage_objects").InSchema(SchemaName);
    }
}
