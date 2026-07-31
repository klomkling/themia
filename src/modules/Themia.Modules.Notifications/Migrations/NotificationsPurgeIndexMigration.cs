using FluentMigrator;

namespace Themia.Modules.Notifications.Migrations;

/// <summary>Adds the composite index the retention purge scans. A NEW migration rather than an edit to
/// <see cref="NotificationsSchemaMigration"/>, which is already deployed — migrations are forward-only.</summary>
[Migration(202607310002, "Themia.Notifications: add outbox purge index")]
public sealed class NotificationsPurgeIndexMigration : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgresql").Execute.Sql(
            "CREATE INDEX ix_outbox_purge ON \"notifications\".\"outbox_messages\" (status, sent_at);");
        IfDatabase("sqlserver").Execute.Sql(
            "CREATE INDEX ix_outbox_purge ON [notifications].[outbox_messages] (status, sent_at);");
        IfDatabase("mysql").Execute.Sql(
            "CREATE INDEX ix_outbox_purge ON outbox_messages (status, sent_at);");
    }

    /// <inheritdoc />
    public override void Down()
    {
        IfDatabase("postgresql").Execute.Sql("DROP INDEX \"notifications\".ix_outbox_purge;");
        IfDatabase("sqlserver").Execute.Sql("DROP INDEX ix_outbox_purge ON [notifications].[outbox_messages];");
        IfDatabase("mysql").Execute.Sql("DROP INDEX ix_outbox_purge ON outbox_messages;");
    }
}
