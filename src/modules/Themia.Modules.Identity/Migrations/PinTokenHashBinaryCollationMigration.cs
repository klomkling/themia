using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Pins a binary collation (<c>Latin1_General_BIN2</c>) on the <c>token_hash</c> columns of
/// <c>identity.user_tokens</c> and <c>identity.refresh_tokens</c> on SQL Server, so equality comparison
/// and the unique indexes are byte-exact rather than case-folding. Token hashes are mixed-case Base64
/// (<c>Convert.ToBase64String</c>); on a SQL Server instance whose default collation is case-insensitive
/// the server would fold case, risking a wrong-row match or a spurious unique-violation between two hashes
/// that differ only by letter case. Forward-only and non-breaking: the Base64 format is unchanged and a
/// binary comparison of two identical strings still equals, so existing tokens continue to match exactly.
/// PostgreSQL needs no change (its <c>text</c> comparison is already case-sensitive).</summary>
[Migration(202606160002, "Themia.Identity: pin binary collation on token_hash for case-exact comparison")]
public sealed class PinTokenHashBinaryCollationMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        // SQL Server can't alter the collation of an indexed column in place: drop the unique index,
        // alter the column to the binary collation, then recreate the unique index. 'identity' is a
        // reserved keyword in SQL Server, so the schema qualifier must be bracketed.
        IfDatabase("sqlserver").Delegate(() =>
        {
            PinBinaryCollation("user_tokens", "ux_user_tokens_token_hash");
            PinBinaryCollation("refresh_tokens", "ux_refresh_tokens_token_hash");
        });

        // PostgreSQL: no-op. text/varchar comparison is case-sensitive by default, so the case-folding
        // hazard this migration fixes does not exist there; no DDL is emitted.

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        // Reverse on SQL Server: drop the unique index, restore the server-default collation, recreate it.
        IfDatabase("sqlserver").Delegate(() =>
        {
            RestoreDefaultCollation("user_tokens", "ux_user_tokens_token_hash");
            RestoreDefaultCollation("refresh_tokens", "ux_refresh_tokens_token_hash");
        });

        // PostgreSQL: no-op (Up was a no-op).
    }

    private void PinBinaryCollation(string table, string uniqueIndex)
    {
        Execute.Sql($"DROP INDEX {uniqueIndex} ON [{SchemaName}].{table};");
        Execute.Sql($"ALTER TABLE [{SchemaName}].{table} ALTER COLUMN token_hash nvarchar(256) COLLATE Latin1_General_BIN2 NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX {uniqueIndex} ON [{SchemaName}].{table} (token_hash);");
    }

    private void RestoreDefaultCollation(string table, string uniqueIndex)
    {
        Execute.Sql($"DROP INDEX {uniqueIndex} ON [{SchemaName}].{table};");
        Execute.Sql($"ALTER TABLE [{SchemaName}].{table} ALTER COLUMN token_hash nvarchar(256) NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX {uniqueIndex} ON [{SchemaName}].{table} (token_hash);");
    }
}
