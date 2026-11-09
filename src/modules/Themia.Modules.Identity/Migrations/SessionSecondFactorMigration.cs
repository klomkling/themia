using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Creates <c>identity.session_second_factor</c>: one row per session (refresh-token family); <c>id</c> is the family id holding the second-factor proof the
/// session passed (amr, strength, time) and its restriction. Forward-only. Sessions open at upgrade have no row, which means no second
/// factor: they fail closed for any action that needs a proof. PostgreSQL and SQL Server only.</summary>
[Migration(202611080001, "Themia.Identity: create session_second_factor")]
public sealed class SessionSecondFactorMigration : Migration
{
    private const string SchemaName = "identity";
    private const string TableName = "session_second_factor";

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe, like the other Identity migrations: adopt a table that is already there.
        if (Schema.Schema(SchemaName).Table(TableName).Exists())
        {
            return;
        }

        IfDatabase("postgresql", "sqlserver").Delegate(CreateTable);

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateTable()
    {
        Create.Table(TableName).InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("amr").AsString(128).NotNullable()
            .WithColumn("strength").AsInt32().NotNullable()
            .WithColumn("authenticated_at").AsDateTimeOffset().NotNullable()
            .WithColumn("restriction").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable();

        Create.Index("ix_session_second_factor_user").OnTable(TableName).InSchema(SchemaName)
            .OnColumn("user_id").Ascending();

        Create.ForeignKey("fk_session_second_factor_user_id").FromTable(TableName).InSchema(SchemaName).ForeignColumn("user_id")
            .ToTable("users").InSchema(SchemaName).PrimaryColumn("id");
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table(TableName).InSchema(SchemaName);
}
