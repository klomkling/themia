using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Conversations.Migrations;

/// <summary>
/// Adds <c>conversation_notify_cursors</c>, the per-recipient state of the notification drain (coord #0182): how far a participant has been
/// notified in a conversation, and the lease that keeps two drainers from pushing to the same recipient. A separate migration because
/// <see cref="ConversationSchemaMigration"/> has shipped and is never edited.
/// </summary>
/// <remarks>
/// <para><b>Composite primary key, no id column:</b> <c>(tenant_id, conversation_id, participant_id)</c>. Tenant isolation is in the key, and the foreign key
/// to the participant binds all three, the same shape as every other child of <c>conversation_participants</c>. The purge deletes this table before the
/// participants, in code.</para>
/// <para><b>Literal table name, byte-exact <c>tenant_id</c> collation</b> pinned before any key (MySQL <c>utf8mb4_bin</c>, SQL Server
/// <c>Latin1_General_BIN2</c>); see <see cref="ConversationSchemaMigration"/>.</para>
/// <para><b>Dates are app-clock UTC.</b> <c>lease_expires_at</c> and <c>updated_at</c> are stamped from the app <see cref="TimeProvider"/>, never the database
/// clock, because <c>conversation_messages.created_at</c> is app-stamped too.</para>
/// </remarks>
[Migration(202611140001, "Themia.Conversations: create the notification cursor table")]
public sealed class ConversationNotifyCursorsMigration : Migration
{
    internal const string CursorsTable = "conversation_notify_cursors";

    private delegate ICreateTableColumnOptionOrWithColumnSyntax ColumnType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe (coord #0078): the table is created in one block, so its presence is the whole migration's presence.
        if (Schema.Table(CursorsTable).Exists())
        {
            return;
        }

        // LOCKSTEP: the per-provider blocks and the unsupported-provider guard below are parallel whitelists; edit both when adding a provider.
        IfDatabase("postgresql").Delegate(() =>
        {
            CreateTable(c => c.AsDateTimeOffset());
            CreateConstraints();
        });
        IfDatabase("mysql").Delegate(() =>
        {
            CreateTable(c => c.AsCustom("DATETIME(6)"));
            PinTenantCollation(quote: c => $"`{c}`", alterVerb: "MODIFY COLUMN", text: n => $"VARCHAR({n})", collation: "utf8mb4_bin");
            CreateConstraints();
        });
        IfDatabase("sqlserver").Delegate(() =>
        {
            CreateTable(c => c.AsDateTimeOffset());
            PinTenantCollation(quote: c => $"[{c}]", alterVerb: "ALTER COLUMN", text: n => $"NVARCHAR({n})", collation: "Latin1_General_BIN2");
            CreateConstraints();
        });

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Conversations supports only PostgreSQL, MySQL, and SQL Server. The active database provider is not " +
                "supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table(CursorsTable);

    private void CreateTable(ColumnType dt)
    {
        var cursors = Create.Table(CursorsTable)
            .WithColumn("tenant_id").AsString(ConversationSchemaMigration.TenantIdWidth).NotNullable()
            .WithColumn("conversation_id").AsGuid().NotNullable()
            .WithColumn("participant_id").AsGuid().NotNullable()
            .WithColumn("notified_seq").AsInt64().NotNullable().WithDefaultValue(0)
            .WithColumn("lease_owner").AsGuid().Nullable();
        dt(cursors.WithColumn("lease_expires_at")).Nullable();
        dt(cursors.WithColumn("created_at")).NotNullable();
        dt(cursors.WithColumn("updated_at")).NotNullable();
    }

    private void CreateConstraints()
    {
        Create.PrimaryKey("pk_conversation_notify_cursors").OnTable(CursorsTable).Columns("tenant_id", "conversation_id", "participant_id");
        Create.ForeignKey("fk_conversation_notify_cursors_participant").FromTable(CursorsTable).ForeignColumns("tenant_id", "conversation_id", "participant_id")
            .ToTable(ConversationSchemaMigration.ParticipantsTable).PrimaryColumns("tenant_id", "conversation_id", "id");
    }

    private void PinTenantCollation(Func<string, string> quote, string alterVerb, Func<int, string> text, string collation) =>
        Execute.Sql($"ALTER TABLE {CursorsTable} {alterVerb} {quote("tenant_id")} {text(ConversationSchemaMigration.TenantIdWidth)} COLLATE {collation} NOT NULL;");
}
