using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Creates <c>identity.refresh_tokens</c> (parent-keyed child of <c>users</c>, no tenant
/// column) on PostgreSQL and SQL Server. Forward-only addition after the 0.5.0 identity schema.</summary>
[Migration(202606150001, "Themia.Identity: create identity.refresh_tokens")]
public sealed class RefreshTokensMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql", "sqlserver").Delegate(CreateRefreshTokens);

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateRefreshTokens()
    {
        Create.Table("refresh_tokens").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("token_hash").AsString(256).NotNullable()
            .WithColumn("family_id").AsGuid().NotNullable()
            .WithColumn("expires_at").AsDateTimeOffset().NotNullable()
            .WithColumn("consumed_at").AsDateTimeOffset().Nullable()
            .WithColumn("revoked_at").AsDateTimeOffset().Nullable()
            .WithColumn("replaced_by_id").AsGuid().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable();

        Create.Index("ix_refresh_tokens_user").OnTable("refresh_tokens").InSchema(SchemaName)
            .OnColumn("user_id").Ascending();

        Create.Index("ix_refresh_tokens_family").OnTable("refresh_tokens").InSchema(SchemaName)
            .OnColumn("family_id").Ascending();

        Create.Index("ux_refresh_tokens_token_hash").OnTable("refresh_tokens").InSchema(SchemaName)
            .OnColumn("token_hash").Ascending().WithOptions().Unique();

        Create.ForeignKey("fk_refresh_tokens_user_id").FromTable("refresh_tokens").InSchema(SchemaName).ForeignColumn("user_id")
            .ToTable("users").InSchema(SchemaName).PrimaryColumn("id");
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("refresh_tokens").InSchema(SchemaName);
}
