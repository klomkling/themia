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
        // 'identity' is a reserved keyword in SQL Server — the schema qualifier must be bracketed.
        IfDatabase("postgres").Delegate(() => CreateFilteredIndexes(SchemaName));
        IfDatabase("sqlserver").Delegate(() => CreateFilteredIndexes($"[{SchemaName}]"));

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

        // A SHA-256 of 32 random bytes is effectively collision-free; uniqueness blocks duplicate/forged
        // hash rows and makes the consume lookup a guaranteed single row.
        Create.Index("ux_user_tokens_token_hash").OnTable("user_tokens").InSchema(SchemaName)
            .OnColumn("token_hash").Ascending().WithOptions().Unique();

        // Child-table referential integrity. No cascade: users/roles are soft-deleted (rows persist),
        // so cascade is unnecessary and risky — restrict (the default) is correct. Fluent Create.ForeignKey
        // is portable across PostgreSQL and SQL Server.
        Create.ForeignKey("fk_user_roles_user_id").FromTable("user_roles").InSchema(SchemaName).ForeignColumn("user_id")
            .ToTable("users").InSchema(SchemaName).PrimaryColumn("id");
        Create.ForeignKey("fk_user_roles_role_id").FromTable("user_roles").InSchema(SchemaName).ForeignColumn("role_id")
            .ToTable("roles").InSchema(SchemaName).PrimaryColumn("id");
        Create.ForeignKey("fk_user_claims_user_id").FromTable("user_claims").InSchema(SchemaName).ForeignColumn("user_id")
            .ToTable("users").InSchema(SchemaName).PrimaryColumn("id");
        Create.ForeignKey("fk_role_claims_role_id").FromTable("role_claims").InSchema(SchemaName).ForeignColumn("role_id")
            .ToTable("roles").InSchema(SchemaName).PrimaryColumn("id");
        Create.ForeignKey("fk_user_tokens_user_id").FromTable("user_tokens").InSchema(SchemaName).ForeignColumn("user_id")
            .ToTable("users").InSchema(SchemaName).PrimaryColumn("id");
    }

    /// <summary>Emits the six per-tenant + platform filtered unique indexes. <paramref name="schema"/> is
    /// the SQL schema qualifier already escaped for the active engine (<c>identity</c> on PostgreSQL,
    /// <c>[identity]</c> on SQL Server, since <c>IDENTITY</c> is a reserved keyword there). The emitted DDL
    /// is otherwise identical across engines.</summary>
    private void CreateFilteredIndexes(string schema)
    {
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_tenant_user_name ON {schema}.users (tenant_id, normalized_user_name) WHERE tenant_id IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_platform_user_name ON {schema}.users (normalized_user_name) WHERE tenant_id IS NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_tenant_email ON {schema}.users (tenant_id, normalized_email) WHERE tenant_id IS NOT NULL AND normalized_email IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_platform_email ON {schema}.users (normalized_email) WHERE tenant_id IS NULL AND normalized_email IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_roles_tenant_name ON {schema}.roles (tenant_id, normalized_name) WHERE tenant_id IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_roles_platform_name ON {schema}.roles (normalized_name) WHERE tenant_id IS NULL;");
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
