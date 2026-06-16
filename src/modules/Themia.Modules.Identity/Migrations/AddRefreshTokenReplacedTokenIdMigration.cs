using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Adds <c>identity.refresh_tokens.replaced_token_id</c> with a filtered unique index used as a
/// concurrency-safe compare-and-set: each rotation inserts a successor carrying its parent's id, so two
/// simultaneous rotations of the same token collide on the index and only one wins. Forward-only addition
/// after the 0.5.1 refresh-tokens schema. PostgreSQL and SQL Server only.</summary>
[Migration(202606160001, "Themia.Identity: add refresh_tokens.replaced_token_id with filtered unique index")]
public sealed class AddRefreshTokenReplacedTokenIdMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql", "sqlserver").Delegate(AddColumn);
        // 'identity' is a reserved keyword in SQL Server — the schema qualifier must be bracketed.
        IfDatabase("postgresql").Delegate(() => CreateFilteredIndex(SchemaName));
        IfDatabase("sqlserver").Delegate(() => CreateFilteredIndex($"[{SchemaName}]"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void AddColumn() =>
        Alter.Table("refresh_tokens").InSchema(SchemaName).AddColumn("replaced_token_id").AsGuid().Nullable();

    /// <summary>Emits the filtered unique index that turns rotation into a compare-and-set. <paramref name="schema"/>
    /// is the SQL schema qualifier already escaped for the active engine (<c>identity</c> on PostgreSQL,
    /// <c>[identity]</c> on SQL Server, since <c>IDENTITY</c> is a reserved keyword there).</summary>
    private void CreateFilteredIndex(string schema) =>
        Execute.Sql($"CREATE UNIQUE INDEX ux_refresh_tokens_replaced_token ON {schema}.refresh_tokens (replaced_token_id) WHERE replaced_token_id IS NOT NULL;");

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Index("ux_refresh_tokens_replaced_token").OnTable("refresh_tokens").InSchema(SchemaName);
        Delete.Column("replaced_token_id").FromTable("refresh_tokens").InSchema(SchemaName);
    }
}
