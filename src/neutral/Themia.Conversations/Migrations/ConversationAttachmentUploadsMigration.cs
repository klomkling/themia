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
        // The provider guard comes before the first schema change, so an unsupported provider is refused with nothing altered.
        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Conversations supports only PostgreSQL, MySQL, and SQL Server. The active database provider is not " +
                "supported; add a migration branch for it."));

        // Replay-safe step by step (coord #0078): MySQL DDL is not transactional, so a run that died after CREATE TABLE leaves the table without its
        // collations or indexes, and the ledger does not record it. Each step checks its own state, so a rerun finishes what the first run did not.
        if (!Schema.Table(ConversationSchemaMigration.AttachmentsTable).Column("file_name").Exists())
        {
            Alter.Table(ConversationSchemaMigration.AttachmentsTable).AddColumn("file_name").AsString(FileNameWidth).NotNullable().WithDefaultValue(string.Empty);
        }

        var tableExists = Schema.Table(UploadsTable).Exists();

        // LOCKSTEP: the per-provider blocks and the unsupported-provider guard above are parallel whitelists; edit both when adding a provider.
        IfDatabase("postgresql").Delegate(() => CreateTable(tableExists, c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() =>
        {
            CreateTable(tableExists, c => c.AsCustom("DATETIME(6)"));
            PinKeyCollations(quote: c => $"`{c}`", alterVerb: "MODIFY COLUMN", text: n => $"VARCHAR({n})", collation: "utf8mb4_bin", collationQuery: MySqlCollationQuery);
        });
        IfDatabase("sqlserver").Delegate(() =>
        {
            CreateTable(tableExists, c => c.AsDateTimeOffset());
            PinKeyCollations(quote: c => $"[{c}]", alterVerb: "ALTER COLUMN", text: n => $"NVARCHAR({n})", collation: "Latin1_General_BIN2", collationQuery: SqlServerCollationQuery);
        });

        CreateIndexIfMissing("ix_conversation_attachment_uploads_created", tableExists, "created_at");
        CreateIndexIfMissing("ix_conversation_attachment_uploads_conversation", tableExists, "tenant_id", "conversation_id");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table(UploadsTable);
        Delete.Column("file_name").FromTable(ConversationSchemaMigration.AttachmentsTable);
    }

    private void CreateTable(bool tableExists, ColumnType dt)
    {
        if (tableExists)
        {
            return;
        }

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
    private const string MySqlCollationQuery =
        "SELECT COLLATION_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c";

    private const string SqlServerCollationQuery =
        "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(@t) AND name = @c";

    private void CreateIndexIfMissing(string name, bool tableExists, string column, string? secondColumn = null)
    {
        if (tableExists && Schema.Table(UploadsTable).Index(name).Exists())
        {
            return;
        }

        var index = Create.Index(name).OnTable(UploadsTable).OnColumn(column).Ascending();
        if (secondColumn is not null)
        {
            index.OnColumn(secondColumn).Ascending();
        }
    }

    /// <summary>
    /// <c>tenant_id</c> as in the main schema, and <c>storage_key</c> to match <c>conversation_attachments.storage_key</c>: purge and erasure UNION the two
    /// tables, and SQL Server refuses a UNION across two collations. The alter runs only when the column is not already pinned: SQL Server refuses to
    /// alter a column an index covers, and a rerun after a partial MySQL run must not fail on work already done.
    /// </summary>
    private void PinKeyCollations(Func<string, string> quote, string alterVerb, Func<int, string> text, string collation, string collationQuery)
    {
        foreach (var (column, width) in new[] { ("tenant_id", ConversationSchemaMigration.TenantIdWidth), ("storage_key", StorageKeyWidth) })
        {
            var sql = $"ALTER TABLE {UploadsTable} {alterVerb} {quote(column)} {text(width)} COLLATE {collation} NOT NULL;";
            Execute.WithConnection((connection, transaction) =>
            {
                using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = collationQuery;
                AddParameter(read, "@t", UploadsTable);
                AddParameter(read, "@c", column);
                if (string.Equals(read.ExecuteScalar() as string, collation, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                using var alter = connection.CreateCommand();
                alter.Transaction = transaction;
                alter.CommandText = sql;
                alter.ExecuteNonQuery();
            });
        }
    }

    private static void AddParameter(System.Data.IDbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
