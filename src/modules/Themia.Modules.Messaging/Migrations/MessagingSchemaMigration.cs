using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Modules.Messaging.Migrations;

/// <summary>Creates the messaging tables — <c>messaging_outbox_messages</c> and
/// <c>messaging_inbox_messages</c> — on PostgreSQL, MySQL, and SQL Server. FluentMigrator is the single
/// DDL authority for both the EF and Dapper data layers (DECISION #6).</summary>
/// <remarks>
/// Uses prefixed table names in the default schema rather than a dedicated <c>messaging</c> schema:
/// FluentMigrator drops <c>InSchema(...)</c> on MySQL (there, "schema" and "database" are the same
/// concept, and the migration runs against whatever database the connection string already selects), so a
/// schema-qualified name means something different per engine — exactly the kind of divergence that let
/// this module's <c>outbox_messages</c> collide with <c>Themia.Modules.Notifications</c>'s identically-named
/// table on MySQL. One literal table name on every engine removes the class of defect instead of patching
/// one instance of it.
/// </remarks>
[Migration(202607310001, "Themia.Messaging: create messaging tables")]
public sealed class MessagingSchemaMigration : Migration
{
    private const string OutboxTable = "messaging_outbox_messages";
    private const string InboxTable = "messaging_inbox_messages";

    /// <summary>Maps a datetime column to the engine-appropriate type. MySQL's FluentMigrator generator
    /// does not support <c>DateTimeOffset</c>, so MySQL uses <c>DATETIME(6)</c> while PostgreSQL and SQL
    /// Server use <c>datetimeoffset</c>, preserving timezone fidelity for the lease and scheduling columns.</summary>
    private delegate ICreateTableColumnOptionOrWithColumnSyntax DateTimeType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTables(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));

        IfDatabase("postgresql").Delegate(() => CreateIndexes($"\"{OutboxTable}\""));
        IfDatabase("sqlserver").Delegate(() => CreateIndexes($"[{OutboxTable}]"));
        IfDatabase("mysql").Delegate(() => CreateIndexes(OutboxTable));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Modules.Messaging supports only PostgreSQL, MySQL, and SQL Server. The active " +
                "database provider is not supported; add a migration branch for it."));
    }

    private void CreateTables(DateTimeType dt)
    {
        // Operational outbox row — not soft-deletable (purged, not tombstoned; the purge is implemented).
        var outbox = Create.Table(OutboxTable)
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

        Create.Index("ix_msg_outbox_tenant").OnTable(OutboxTable)
            .OnColumn("tenant_id").Ascending();

        // The same logical message fanned out to two peers legitimately shares a message_id — each
        // receiver dedups on (origin, message_id) independently — but enqueuing it twice for the SAME
        // destination is a double-publish bug, caught here rather than at the far end.
        Create.Index("ux_msg_outbox_message_destination").OnTable(OutboxTable)
            .OnColumn("message_id").Ascending().OnColumn("destination").Ascending()
            .WithOptions().Unique();

        // Admission records. The composite PK IS the deduplication guarantee.
        var inbox = Create.Table(InboxTable)
            .WithColumn("origin").AsString(100).NotNullable().PrimaryKey()
            .WithColumn("message_id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("type").AsString(200).NotNullable();
        dt(inbox.WithColumn("received_at")).NotNullable();

        Create.Index("ix_msg_inbox_received").OnTable(InboxTable)
            .OnColumn("received_at").Ascending();
    }

    /// <summary>Creates the composite indexes the claim and purge queries scan.
    /// <paramref name="table"/> is the engine-quoted outbox table identifier — no user input is
    /// interpolated, only the fixed identifier.</summary>
    private void CreateIndexes(string table)
    {
        Execute.Sql($"CREATE INDEX ix_msg_outbox_claim ON {table} (status, next_attempt_at);");
        Execute.Sql($"CREATE INDEX ix_msg_outbox_purge ON {table} (status, sent_at);");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table(InboxTable);
        Delete.Table(OutboxTable);
    }
}
