using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Adds <c>identity.refresh_tokens.successor_ciphertext</c>, a nullable column that holds, only while the refresh reuse grace is on,
/// the successor's raw token encrypted under a key derived from the rotated token's own raw value. Forward-only, nullable, no index, no
/// default: existing rows and a deployment that never enables the grace are unaffected. PostgreSQL and SQL Server only.</summary>
[Migration(202610310001, "Themia.Identity: add refresh_tokens.successor_ciphertext (nullable)")]
public sealed class AddRefreshTokenSuccessorCiphertextMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe, like the other Identity migrations: adopt a column that is already there.
        if (Schema.Schema(SchemaName).Table("refresh_tokens").Column("successor_ciphertext").Exists())
        {
            return;
        }

        IfDatabase("postgresql", "sqlserver").Delegate(() =>
            Alter.Table("refresh_tokens").InSchema(SchemaName).AddColumn("successor_ciphertext").AsString(256).Nullable());

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down() =>
        Delete.Column("successor_ciphertext").FromTable("refresh_tokens").InSchema(SchemaName);
}
