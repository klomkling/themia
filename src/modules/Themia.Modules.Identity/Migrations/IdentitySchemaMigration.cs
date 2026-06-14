using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Creates the <c>identity</c> schema and tables (users, roles, memberships, claims, tokens) with per-tenant + platform filtered unique indexes, on PostgreSQL and SQL Server.</summary>
[Migration(202606140001, "Themia.Identity: create identity schema and tables")]
public sealed class IdentitySchemaMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgres", "sqlserver").Delegate(CreateSchemaAndTables);
        IfDatabase("postgres", "sqlserver").Delegate(CreateFilteredIndexes);

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateSchemaAndTables()
    {
        if (!Schema.Schema(SchemaName).Exists())
        {
            Create.Schema(SchemaName);
        }

        Create.Table("users").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_name").AsString(256).NotNullable()
            .WithColumn("normalized_user_name").AsString(256).NotNullable()
            .WithColumn("email").AsString(256).Nullable()
            .WithColumn("normalized_email").AsString(256).Nullable()
            .WithColumn("email_confirmed").AsBoolean().NotNullable()
            .WithColumn("phone_number").AsString(64).Nullable()
            .WithColumn("phone_number_confirmed").AsBoolean().NotNullable()
            .WithColumn("password_hash").AsString(1024).Nullable()
            .WithColumn("security_stamp").AsString(128).NotNullable()
            .WithColumn("is_active").AsBoolean().NotNullable()
            .WithColumn("access_failed_count").AsInt32().NotNullable()
            .WithColumn("lockout_end").AsDateTimeOffset().Nullable()
            .WithColumn("lockout_enabled").AsBoolean().NotNullable()
            .WithColumn("two_factor_enabled").AsBoolean().NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable()
            .WithColumn("is_deleted").AsBoolean().NotNullable()
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_by").AsString(100).Nullable()
            .WithColumn("restored_at").AsDateTimeOffset().Nullable()
            .WithColumn("restored_by").AsString(100).Nullable();

        Create.Table("roles").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("name").AsString(256).NotNullable()
            .WithColumn("normalized_name").AsString(256).NotNullable()
            .WithColumn("description").AsString(512).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable()
            .WithColumn("is_deleted").AsBoolean().NotNullable()
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_by").AsString(100).Nullable()
            .WithColumn("restored_at").AsDateTimeOffset().Nullable()
            .WithColumn("restored_by").AsString(100).Nullable();

        Create.Table("user_roles").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("role_id").AsGuid().NotNullable();

        Create.Table("user_claims").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("claim_type").AsString(256).NotNullable()
            .WithColumn("claim_value").AsString(1024).NotNullable();

        Create.Table("role_claims").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("role_id").AsGuid().NotNullable()
            .WithColumn("claim_type").AsString(256).NotNullable()
            .WithColumn("claim_value").AsString(1024).NotNullable();

        Create.Table("user_tokens").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("purpose").AsInt32().NotNullable()
            .WithColumn("token_hash").AsString(256).NotNullable()
            .WithColumn("expires_at").AsDateTimeOffset().NotNullable()
            .WithColumn("consumed_at").AsDateTimeOffset().Nullable();

        // Non-filtered lookup indexes (FK access paths) — fluent API is portable for these.
        Create.Index("ix_user_roles_user").OnTable("user_roles").InSchema(SchemaName)
            .OnColumn("user_id").Ascending();
        Create.Index("ix_user_claims_user").OnTable("user_claims").InSchema(SchemaName)
            .OnColumn("user_id").Ascending();
        Create.Index("ix_role_claims_role").OnTable("role_claims").InSchema(SchemaName)
            .OnColumn("role_id").Ascending();
        Create.Index("ix_user_tokens_user_purpose").OnTable("user_tokens").InSchema(SchemaName)
            .OnColumn("user_id").Ascending().OnColumn("purpose").Ascending();

        // No-duplicate-membership: a plain unique index (no NULLs involved).
        Create.Index("ux_user_roles_user_role").OnTable("user_roles").InSchema(SchemaName)
            .OnColumn("user_id").Ascending().OnColumn("role_id").Ascending()
            .WithOptions().Unique();
    }

    private void CreateFilteredIndexes()
    {
        // Two filtered unique indexes per "named" table: one scoping uniqueness within a tenant,
        // one enforcing global uniqueness among platform (tenant_id IS NULL) rows. Identical syntax
        // on PostgreSQL and SQL Server.
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_tenant_user_name ON {SchemaName}.users (tenant_id, normalized_user_name) WHERE tenant_id IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_platform_user_name ON {SchemaName}.users (normalized_user_name) WHERE tenant_id IS NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_tenant_email ON {SchemaName}.users (tenant_id, normalized_email) WHERE tenant_id IS NOT NULL AND normalized_email IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_platform_email ON {SchemaName}.users (normalized_email) WHERE tenant_id IS NULL AND normalized_email IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_roles_tenant_name ON {SchemaName}.roles (tenant_id, normalized_name) WHERE tenant_id IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_roles_platform_name ON {SchemaName}.roles (normalized_name) WHERE tenant_id IS NULL;");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("user_tokens").InSchema(SchemaName);
        Delete.Table("role_claims").InSchema(SchemaName);
        Delete.Table("user_claims").InSchema(SchemaName);
        Delete.Table("user_roles").InSchema(SchemaName);
        Delete.Table("roles").InSchema(SchemaName);
        Delete.Table("users").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }
}
