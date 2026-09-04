using FluentMigrator;

namespace Themia.Modules.Notifications.Migrations;

/// <summary>Adds <c>outbox_messages.delivery_options</c>, the JSON column carrying a queued message's
/// cc, bcc, text/plain alternative and headers. A NEW migration rather than an edit to
/// <see cref="NotificationsSchemaMigration"/>, which is already deployed — migrations are
/// forward-only.</summary>
[Migration(202609040003, "Themia.Notifications: add outbox delivery_options")]
public sealed class NotificationsDeliveryOptionsMigration : Migration
{
    private const string SchemaName = "notifications";
    private const string TableName = "outbox_messages";
    private const string ColumnName = "delivery_options";

    /// <inheritdoc />
    public override void Up()
    {
        // Adopt-if-exists, per coord #0085 and #0096. The per-assembly version ledger (#0078) starts
        // EMPTY on every database that predates it, so this Up() can run against a column that is
        // already there — an unguarded ADD COLUMN then fails and crash-loops the host at boot, which is
        // exactly the outage those two tickets describe. Nullable with no default, so adding it neither
        // rewrites existing rows nor changes what they mean: NULL is "this message set no options",
        // which is true of every row written before today.
        IfDatabase("postgresql").Delegate(() => AddColumnIfMissing(qualified: true));
        IfDatabase("sqlserver").Delegate(() => AddColumnIfMissing(qualified: true));

        // MySQL folds schema into the database the connection string already selects, so FluentMigrator
        // drops InSchema there and the column must be looked up on the unqualified table.
        IfDatabase("mysql").Delegate(() => AddColumnIfMissing(qualified: false));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Notifications supports only PostgreSQL, MySQL, and SQL Server. The active " +
                "database provider is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        IfDatabase("postgresql").Delegate(() => Delete.Column(ColumnName).FromTable(TableName).InSchema(SchemaName));
        IfDatabase("sqlserver").Delegate(() => Delete.Column(ColumnName).FromTable(TableName).InSchema(SchemaName));
        IfDatabase("mysql").Delegate(() => Delete.Column(ColumnName).FromTable(TableName));
    }

    private void AddColumnIfMissing(bool qualified)
    {
        var exists = qualified
            ? Schema.Schema(SchemaName).Table(TableName).Column(ColumnName).Exists()
            : Schema.Table(TableName).Column(ColumnName).Exists();

        if (exists) return;

        var alter = qualified
            ? Alter.Table(TableName).InSchema(SchemaName)
            : Alter.Table(TableName);

        alter.AddColumn(ColumnName).AsString(int.MaxValue).Nullable();
    }
}
