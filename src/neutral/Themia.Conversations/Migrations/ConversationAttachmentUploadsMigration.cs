using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Conversations.Migrations;

/// <summary>
/// Adds what attaching a file to a post needs (coord #0182): <c>conversation_attachments.file_name</c> (the name the download is served under) and
/// <c>conversation_attachment_uploads</c>, the staging table of storage keys. A separate migration because <see cref="ConversationSchemaMigration"/> has
/// shipped and is never edited.
/// </summary>
/// <remarks>
/// <para><b>Why a staging table:</b> the file is written to storage before the post transaction, so a crash or a failed cleanup would leave an object with
/// no attachment row, and an erasure or purge reads keys from rows. A row here is committed <i>before</i> the write and deleted inside the post
/// transaction, so every key that can exist in storage is reachable from the database. The sweeper deletes the object and the row of any entry older
/// than <see cref="ConversationOptions.AttachmentUploadGrace"/>; erasure and purge delete the keys of their conversation.</para>
/// <para>No foreign keys: the staging row must survive the conversation row so a purge can still find a leaked key, and the purge deletes it in code.
/// <b>Literal table name, byte-exact <c>tenant_id</c> collation</b>, as in <see cref="ConversationSchemaMigration"/>.</para>
/// </remarks>
[Migration(202611150001, "Themia.Conversations: attachment file names and the upload staging table")]
public sealed class ConversationAttachmentUploadsMigration : Migration
{
    internal const string UploadsTable = "conversation_attachment_uploads";
    internal const int FileNameWidth = 255;
    private const int StorageKeyWidth = 300;

    private delegate ICreateTableColumnOptionOrWithColumnSyntax ColumnType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe (coord #0078): the staging table is created last, so its presence is the whole migration's presence.
        if (Schema.Table(UploadsTable).Exists())
        {
            return;
        }

        // The provider guard comes before the first schema change, so an unsupported provider is refused with nothing altered.
        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Conversations supports only PostgreSQL, MySQL, and SQL Server. The active database provider is not " +
                "supported; add a migration branch for it."));

        if (!Schema.Table(ConversationSchemaMigration.AttachmentsTable).Column("file_name").Exists())
        {
            Alter.Table(ConversationSchemaMigration.AttachmentsTable).AddColumn("file_name").AsString(FileNameWidth).NotNullable().WithDefaultValue(string.Empty);
        }

        // LOCKSTEP: the per-provider blocks and the unsupported-provider guard above are parallel whitelists; edit both when adding a provider.
        IfDatabase("postgresql").Delegate(() => CreateTable(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() =>
        {
            CreateTable(c => c.AsCustom("DATETIME(6)"));
            PinKeyCollations(quote: c => $"`{c}`", alterVerb: "MODIFY COLUMN", text: n => $"VARCHAR({n})", collation: "utf8mb4_bin");
        });
        IfDatabase("sqlserver").Delegate(() =>
        {
            CreateTable(c => c.AsDateTimeOffset());
            PinKeyCollations(quote: c => $"[{c}]", alterVerb: "ALTER COLUMN", text: n => $"NVARCHAR({n})", collation: "Latin1_General_BIN2");
        });

        Create.Index("ix_conversation_attachment_uploads_created").OnTable(UploadsTable).OnColumn("created_at").Ascending();
        Create.Index("ix_conversation_attachment_uploads_conversation").OnTable(UploadsTable).OnColumn("tenant_id").Ascending().OnColumn("conversation_id").Ascending();
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table(UploadsTable);
        Delete.Column("file_name").FromTable(ConversationSchemaMigration.AttachmentsTable);
    }

    private void CreateTable(ColumnType dt)
    {
        var uploads = Create.Table(UploadsTable)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey("pk_conversation_attachment_uploads")
            .WithColumn("tenant_id").AsString(ConversationSchemaMigration.TenantIdWidth).NotNullable()
            .WithColumn("conversation_id").AsGuid().NotNullable()
            .WithColumn("owner_participant_id").AsGuid().NotNullable()
            .WithColumn("storage_key").AsString(StorageKeyWidth).NotNullable();
        dt(uploads.WithColumn("created_at")).NotNullable();
    }

    /// <summary>
    /// <c>tenant_id</c> as in the main schema, and <c>storage_key</c> to match <c>conversation_attachments.storage_key</c>: purge and erasure UNION the two
    /// tables, and SQL Server refuses a UNION across two collations.
    /// </summary>
    private void PinKeyCollations(Func<string, string> quote, string alterVerb, Func<int, string> text, string collation)
    {
        foreach (var (column, width) in new[] { ("tenant_id", ConversationSchemaMigration.TenantIdWidth), ("storage_key", StorageKeyWidth) })
        {
            Execute.Sql($"ALTER TABLE {UploadsTable} {alterVerb} {quote(column)} {text(width)} COLLATE {collation} NOT NULL;");
        }
    }
}
