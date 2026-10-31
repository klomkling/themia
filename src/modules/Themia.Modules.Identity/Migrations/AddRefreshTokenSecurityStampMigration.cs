using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Adds <c>identity.refresh_tokens.security_stamp</c>, a nullable column holding the user's security stamp a refresh
/// session is bound to, so a password reset or reactivation ends sessions that predate it. Forward-only, nullable, no index, no
/// default: rows issued before it stay NULL and are accepted once, then bound. Same length as <c>users.security_stamp</c>.
/// PostgreSQL and SQL Server only.</summary>
[Migration(202610310002, "Themia.Identity: add refresh_tokens.security_stamp (nullable)")]
public sealed class AddRefreshTokenSecurityStampMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe, like the other Identity migrations: adopt a column that is already there.
        if (Schema.Schema(SchemaName).Table("refresh_tokens").Column("security_stamp").Exists())
        {
            return;
        }

        IfDatabase("postgresql", "sqlserver").Delegate(() =>
            Alter.Table("refresh_tokens").InSchema(SchemaName).AddColumn("security_stamp").AsString(128).Nullable());

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down() =>
        Delete.Column("security_stamp").FromTable("refresh_tokens").InSchema(SchemaName);
}
