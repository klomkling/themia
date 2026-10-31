using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Adds <c>identity.refresh_tokens.security_stamp</c>, a nullable column holding the user's security stamp a refresh session is
/// bound to, so a password reset or reactivation ends sessions that predate it. The sessions live at upgrade time are bound to their
/// user's current stamp here. Forward-only, no index, no default; a row written later by a node that does not know the column stays NULL and
/// is bound at its next refresh. Same length as <c>users.security_stamp</c>. PostgreSQL and SQL Server only.</summary>
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

        // Bind the sessions open today to their user's current stamp, so the first credential event after the upgrade ends them
        // instead of finding an unbound row. Live rows only; a row issued later by a node that predates this column stays NULL.
        IfDatabase("postgresql").Execute.Sql(
            "UPDATE identity.refresh_tokens SET security_stamp = u.security_stamp FROM identity.users u " +
            "WHERE u.id = refresh_tokens.user_id AND refresh_tokens.revoked_at IS NULL AND refresh_tokens.security_stamp IS NULL;");
        IfDatabase("sqlserver").Execute.Sql(
            "UPDATE rt SET rt.security_stamp = u.security_stamp FROM [identity].refresh_tokens rt " +
            "JOIN [identity].users u ON u.id = rt.user_id WHERE rt.revoked_at IS NULL AND rt.security_stamp IS NULL;");

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
