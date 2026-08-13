using FluentMigrator;

namespace Themia.Modules.Notifications.Migrations;

/// <summary>Adds the composite index the retention purge scans. A NEW migration rather than an edit to
/// <see cref="NotificationsSchemaMigration"/>, which is already deployed — migrations are forward-only.</summary>
[Migration(202607310002, "Themia.Notifications: add outbox purge index")]
public sealed class NotificationsPurgeIndexMigration : Migration
{
    /// <inheritdoc />
    private const string SchemaName = "notifications";
    private const string IndexName = "ix_outbox_purge";

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe (coord #0078). No engine here supports CREATE INDEX IF NOT EXISTS across the board —
        // PostgreSQL does, MySQL and SQL Server do not — so existence is checked before the statement is
        // enqueued. The check is per engine for the same reason the SQL is: FluentMigrator drops InSchema
        // on MySQL, where schema and database are one concept, so the index lives on an unqualified table.
        IfDatabase("postgresql").Delegate(() =>
        {
            if (!Schema.Schema(SchemaName).Table("outbox_messages").Index(IndexName).Exists())
            {
                Execute.Sql($"CREATE INDEX {IndexName} ON \"{SchemaName}\".\"outbox_messages\" (status, sent_at);");
            }
        });

        IfDatabase("sqlserver").Delegate(() =>
        {
            if (!Schema.Schema(SchemaName).Table("outbox_messages").Index(IndexName).Exists())
            {
                Execute.Sql($"CREATE INDEX {IndexName} ON [{SchemaName}].[outbox_messages] (status, sent_at);");
            }
        });

        IfDatabase("mysql").Delegate(() =>
        {
            if (!Schema.Table("outbox_messages").Index(IndexName).Exists())
            {
                Execute.Sql($"CREATE INDEX {IndexName} ON outbox_messages (status, sent_at);");
            }
        });
    }

    /// <inheritdoc />
    public override void Down()
    {
        IfDatabase("postgresql").Execute.Sql("DROP INDEX \"notifications\".ix_outbox_purge;");
        IfDatabase("sqlserver").Execute.Sql("DROP INDEX ix_outbox_purge ON [notifications].[outbox_messages];");
        IfDatabase("mysql").Execute.Sql("DROP INDEX ix_outbox_purge ON outbox_messages;");
    }
}
