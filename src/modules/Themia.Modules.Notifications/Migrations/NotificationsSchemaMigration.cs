using System;
using FluentMigrator;

namespace Themia.Modules.Notifications.Migrations;

/// <summary>Creates the <c>notifications</c> schema and its four tables (<c>outbox_messages</c>,
/// <c>in_app_notifications</c>, <c>notification_preferences</c>, <c>tenant_provider_configs</c>) on
/// PostgreSQL, MySQL, and SQL Server. FluentMigrator is the single DDL authority for both the EF and
/// Dapper data layers (DECISION #6).</summary>
[Migration(202606220001, "Themia.Notifications: create notifications schema and tables")]
public sealed class NotificationsSchemaMigration : Migration
{
    private const string SchemaName = "notifications";

    /// <inheritdoc />
    public override void Up()
    {
        // On MySQL FluentMigrator maps InSchema("notifications") to the database name, which the
        // connection string already selects, so the schema call is effectively a no-op there.
        IfDatabase("postgresql", "mysql", "sqlserver").Delegate(CreateTables);

        // The composite claim-query index (status, next_attempt_at) is created via raw SQL because the
        // table identifier must be schema-qualified per engine.
        IfDatabase("postgresql").Delegate(() => CreateClaimIndex("\"notifications\".\"outbox_messages\""));
        IfDatabase("sqlserver").Delegate(() => CreateClaimIndex("[notifications].[outbox_messages]"));
        IfDatabase("mysql").Delegate(() => CreateClaimIndex("outbox_messages"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Notifications supports only PostgreSQL, MySQL, and SQL Server. The active " +
                "database provider is not supported; add a migration branch for it."));
    }

    private void CreateTables()
    {
        if (!Schema.Schema(SchemaName).Exists())
        {
            Create.Schema(SchemaName);
        }

        // Operational outbox row — not soft-deletable (purged, not tombstoned).
        Create.Table("outbox_messages").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("channel").AsInt32().NotNullable()
            .WithColumn("recipient").AsString(512).NotNullable()
            .WithColumn("subject").AsString(1024).Nullable()
            .WithColumn("body").AsString(int.MaxValue).NotNullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("attempts").AsInt32().NotNullable()
            .WithColumn("next_attempt_at").AsDateTimeOffset().NotNullable()
            .WithColumn("scheduled_for").AsDateTimeOffset().Nullable()
            .WithColumn("lease_owner").AsString(100).Nullable()
            .WithColumn("lease_expires_at").AsDateTimeOffset().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("sent_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_error").AsString(int.MaxValue).Nullable();

        Create.Index("ix_outbox_tenant").OnTable("outbox_messages").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending();

        Create.Table("in_app_notifications").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsString(100).NotNullable()
            .WithColumn("title").AsString(512).NotNullable()
            .WithColumn("body").AsString(int.MaxValue).NotNullable()
            .WithColumn("is_read").AsBoolean().NotNullable()
            .WithColumn("read_at").AsDateTimeOffset().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable();

        Create.Index("ix_in_app_tenant_user").OnTable("in_app_notifications").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending().OnColumn("user_id").Ascending().OnColumn("is_read").Ascending();

        Create.Table("notification_preferences").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsString(100).Nullable()
            .WithColumn("channel").AsInt32().NotNullable()
            .WithColumn("is_enabled").AsBoolean().NotNullable()
            .WithColumn("locale").AsString(20).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable();

        Create.Index("ix_pref_tenant_user_channel").OnTable("notification_preferences").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending().OnColumn("user_id").Ascending().OnColumn("channel").Ascending();

        Create.Table("tenant_provider_configs").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("channel").AsInt32().NotNullable()
            .WithColumn("host").AsString(256).Nullable()
            .WithColumn("port").AsInt32().Nullable()
            .WithColumn("username").AsString(256).Nullable()
            .WithColumn("password").AsString(512).Nullable()
            .WithColumn("from_address").AsString(256).Nullable()
            .WithColumn("use_ssl").AsBoolean().NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable();

        Create.Index("ix_provider_tenant_channel").OnTable("tenant_provider_configs").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending().OnColumn("channel").Ascending();
    }

    /// <summary>Creates the composite index the per-engine claim query scans (status, next_attempt_at).
    /// <paramref name="table"/> is the engine-quoted, schema-qualified table identifier — no user input
    /// is interpolated, only the fixed identifier.</summary>
    private void CreateClaimIndex(string table)
    {
        Execute.Sql($"CREATE INDEX ix_outbox_claim ON {table} (status, next_attempt_at);");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("tenant_provider_configs").InSchema(SchemaName);
        Delete.Table("notification_preferences").InSchema(SchemaName);
        Delete.Table("in_app_notifications").InSchema(SchemaName);
        Delete.Table("outbox_messages").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }
}
