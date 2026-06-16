using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Creates <c>identity.external_logins</c> (tenant-scoped child of <c>users</c>) with per-tenant +
/// platform filtered unique indexes on (provider, external_id), on PostgreSQL and SQL Server. Forward-only
/// addition after the 0.5.1 refresh-tokens schema.</summary>
[Migration(202606160003, "Themia.Identity: create identity.external_logins")]
public sealed class ExternalLoginsMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql", "sqlserver").Delegate(CreateExternalLogins);
        // 'identity' is a reserved keyword in SQL Server — the schema qualifier must be bracketed.
        IfDatabase("postgresql").Delegate(() => CreateFilteredIndexes(SchemaName));
        IfDatabase("sqlserver").Delegate(() => CreateFilteredIndexes($"[{SchemaName}]"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateExternalLogins()
    {
        Create.Table("external_logins").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("provider").AsString(64).NotNullable()
            .WithColumn("external_id").AsString(256).NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable();

        Create.Index("ix_external_logins_user").OnTable("external_logins").InSchema(SchemaName)
            .OnColumn("user_id").Ascending();

        // Child-table referential integrity. No cascade: users are soft-deleted (rows persist), so restrict
        // (the default) is correct. Fluent Create.ForeignKey is portable across PostgreSQL and SQL Server.
        Create.ForeignKey("fk_external_logins_user_id").FromTable("external_logins").InSchema(SchemaName).ForeignColumn("user_id")
            .ToTable("users").InSchema(SchemaName).PrimaryColumn("id");
    }

    /// <summary>Emits the per-tenant + platform filtered unique indexes on (provider, external_id).
    /// <paramref name="schema"/> is the SQL schema qualifier already escaped for the active engine
    /// (<c>identity</c> on PostgreSQL, <c>[identity]</c> on SQL Server, since <c>IDENTITY</c> is a reserved
    /// keyword there). The emitted DDL is otherwise identical across engines.</summary>
    private void CreateFilteredIndexes(string schema)
    {
        Execute.Sql($"CREATE UNIQUE INDEX ux_external_logins_tenant_provider_key ON {schema}.external_logins (tenant_id, provider, external_id) WHERE tenant_id IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_external_logins_platform_provider_key ON {schema}.external_logins (provider, external_id) WHERE tenant_id IS NULL;");
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("external_logins").InSchema(SchemaName);
}
