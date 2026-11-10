using FluentMigrator;

namespace Themia.Exceptional.Migrations;

/// <summary>
/// Adds the <c>ExceptionAlertState</c> table (one row per alerted exception row and trigger; its unique key is the
/// cross-instance idempotency guard) and an <c>Exceptions (ApplicationName, LastLogDate)</c> index for the alert
/// candidate query. Additive; touches no existing column.
/// </summary>
[Migration(202611100002, "Themia.Exceptional: add ExceptionAlertState table and LastLogDate index")]
public sealed class AddExceptionAlertState : Migration
{
    private const string StateTable = "ExceptionAlertState";
    private const string UniqueIndex = "IX_ExceptionAlertState_Exception_Trigger";
    private const string LastLogIndex = "IX_Exceptions_App_LastLog";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase(p =>
                !p.StartsWith("Postgres", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", System.StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new System.NotSupportedException(
                "Themia.Exceptional supports only PostgreSQL, MySQL, and SQL Server."));

        // Replay-safe, like the other Exceptional migrations: adopt what is already there. Every index has its own
        // guard because MySQL DDL is not transactional: a run that died after CREATE TABLE must still get its
        // unique key on the re-run (it is the cross-instance idempotency guard).
        if (!Schema.Table(StateTable).Exists())
        {
            // LOCKSTEP with ExceptionLogMigration's provider whitelist (PostgreSQL/MySQL/SqlServer).
            IfDatabase("postgresql").Delegate(() => CreateState(c => c.AsDateTimeOffset()));
            IfDatabase("mysql").Delegate(() => CreateState(c => c.AsCustom("DATETIME(6)")));
            IfDatabase("sqlserver").Delegate(() => CreateState(c => c.AsDateTime2()));
        }

        // No foreign key to Exceptions: purging old exceptions must neither cascade into nor be blocked by alert history.
        CreateStateIndex(UniqueIndex, t => t.OnColumn("ExceptionId").Ascending().OnColumn("AlertTrigger").Ascending(), unique: true);
        CreateStateIndex("IX_ExceptionAlertState_Batch", t => t.OnColumn("BatchId").Ascending());
        CreateStateIndex("IX_ExceptionAlertState_Status_Sent", t => t.OnColumn("Status").Ascending().OnColumn("SentAt").Ascending());
        CreateStateIndex("IX_ExceptionAlertState_App_Hash", t => t
            .OnColumn("ApplicationName").Ascending().OnColumn("ErrorHash").Ascending()
            .OnColumn("AlertTrigger").Ascending().OnColumn("ClaimedAt").Ascending());

        if (!Schema.Table("Exceptions").Index(LastLogIndex).Exists())
        {
            IfDatabase("postgresql", "mysql", "sqlserver")
                .Create.Index(LastLogIndex).OnTable("Exceptions")
                .OnColumn("ApplicationName").Ascending()
                .OnColumn("LastLogDate").Ascending();
        }
    }

    private void CreateStateIndex(
        string name,
        System.Func<FluentMigrator.Builders.Create.Index.ICreateIndexOnColumnSyntax,
            FluentMigrator.Builders.Create.Index.ICreateIndexOnColumnSyntax> columns,
        bool unique = false)
    {
        if (Schema.Table(StateTable).Index(name).Exists())
            return;
        var on = Create.Index(name).OnTable(StateTable);
        columns(on);
        if (unique)
            on.WithOptions().Unique();
    }

    private void CreateState(
        System.Func<FluentMigrator.Builders.Create.Table.ICreateTableColumnAsTypeSyntax,
            FluentMigrator.Builders.Create.Table.ICreateTableColumnOptionOrWithColumnSyntax> timestamp)
    {
        var table = Create.Table(StateTable)
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("ExceptionId").AsInt64().NotNullable()
            .WithColumn("AlertTrigger").AsInt32().NotNullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("BatchId").AsGuid().NotNullable()
            .WithColumn("ApplicationName").AsString(256).NotNullable()
            .WithColumn("ErrorHash").AsString(64).NotNullable()
            .WithColumn("Attempts").AsInt32().NotNullable().WithDefaultValue(1);
        timestamp(table.WithColumn("ClaimedAt")).NotNullable();
        timestamp(table.WithColumn("SentAt")).Nullable();
    }

    /// <inheritdoc />
    public override void Down()
    {
        IfDatabase("postgresql", "mysql", "sqlserver").Delete.Index(LastLogIndex).OnTable("Exceptions");
        Delete.Table(StateTable);
    }
}
