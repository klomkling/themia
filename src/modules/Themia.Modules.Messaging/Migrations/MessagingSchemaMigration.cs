using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Modules.Messaging.Migrations;

/// <summary>Creates the <c>messaging</c> schema and its two tables (<c>outbox_messages</c>,
/// <c>inbox_messages</c>) on PostgreSQL, MySQL, and SQL Server. FluentMigrator is the single DDL
/// authority for both the EF and Dapper data layers (DECISION #6).</summary>
[Migration(202607310001, "Themia.Messaging: create messaging schema and tables")]
public sealed class MessagingSchemaMigration : Migration
{
    private const string SchemaName = "messaging";

    /// <summary>Maps a datetime column to the engine-appropriate type. MySQL's FluentMigrator generator
    /// does not support <c>DateTimeOffset</c>, so MySQL uses <c>DATETIME(6)</c> while PostgreSQL and SQL
    /// Server use <c>datetimeoffset</c>, preserving timezone fidelity for the lease and scheduling columns.</summary>
    private delegate ICreateTableColumnOptionOrWithColumnSyntax DateTimeType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        Create.Schema(SchemaName);

        IfDatabase("postgresql").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTables(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));

        IfDatabase("postgresql").Delegate(() => CreateIndexes("\"messaging\".\"outbox_messages\""));
        IfDatabase("sqlserver").Delegate(() => CreateIndexes("[messaging].[outbox_messages]"));
        IfDatabase("mysql").Delegate(() => CreateIndexes("outbox_messages"));
    }

    private void CreateTables(DateTimeType dt)
    {
        // Operational outbox row — not soft-deletable (purged, not tombstoned; the purge is implemented).
        var outbox = Create.Table("outbox_messages").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("message_id").AsGuid().NotNullable()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("type").AsString(200).NotNullable()
            .WithColumn("payload").AsString(int.MaxValue).NotNullable()
            .WithColumn("destination").AsString(100).NotNullable()
            .WithColumn("origin").AsString(100).NotNullable()
            .WithColumn("entity_key").AsString(200).Nullable()
            .WithColumn("version").AsInt64().Nullable()
            .WithColumn("headers").AsString(int.MaxValue).Nullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("attempts").AsInt32().NotNullable();
        dt(outbox.WithColumn("next_attempt_at")).NotNullable();
        dt(outbox.WithColumn("scheduled_for")).Nullable();
        outbox.WithColumn("lease_owner").AsString(100).Nullable();
        dt(outbox.WithColumn("lease_expires_at")).Nullable();
        dt(outbox.WithColumn("created_at")).NotNullable();
        dt(outbox.WithColumn("sent_at")).Nullable();
        outbox.WithColumn("last_error").AsString(int.MaxValue).Nullable();

        Create.Index("ix_msg_outbox_tenant").OnTable("outbox_messages").InSchema(SchemaName)
            .OnColumn("tenant_id").Ascending();

        // The same logical message fanned out to two peers legitimately shares a message_id — each
        // receiver dedups on (origin, message_id) independently — but enqueuing it twice for the SAME
        // destination is a double-publish bug, caught here rather than at the far end.
        Create.Index("ux_msg_outbox_message_destination").OnTable("outbox_messages").InSchema(SchemaName)
            .OnColumn("message_id").Ascending().OnColumn("destination").Ascending()
            .WithOptions().Unique();

        // Admission records. The composite PK IS the deduplication guarantee.
        var inbox = Create.Table("inbox_messages").InSchema(SchemaName)
            .WithColumn("origin").AsString(100).NotNullable().PrimaryKey()
            .WithColumn("message_id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("type").AsString(200).NotNullable();
        dt(inbox.WithColumn("received_at")).NotNullable();

        Create.Index("ix_msg_inbox_received").OnTable("inbox_messages").InSchema(SchemaName)
            .OnColumn("received_at").Ascending();
    }

    /// <summary>Creates the composite indexes the claim and purge queries scan.
    /// <paramref name="table"/> is the engine-quoted, schema-qualified identifier — no user input is
    /// interpolated, only the fixed identifier.</summary>
    private void CreateIndexes(string table)
    {
        Execute.Sql($"CREATE INDEX ix_msg_outbox_claim ON {table} (status, next_attempt_at);");
        Execute.Sql($"CREATE INDEX ix_msg_outbox_purge ON {table} (status, sent_at);");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("inbox_messages").InSchema(SchemaName);
        Delete.Table("outbox_messages").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }
}
