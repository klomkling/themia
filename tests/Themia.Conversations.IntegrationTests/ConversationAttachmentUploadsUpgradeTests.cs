using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Themia.Conversations.Migrations;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Conversations.IntegrationTests;

/// <summary>
/// The attachments migration (202611150001) is forward-only and runs on databases that already hold conversations, and on MySQL a run that died halfway
/// leaves DDL behind that the ledger does not record. These tests build the databases a real upgrade meets, on every engine.
/// </summary>
public abstract class ConversationAttachmentUploadsUpgradeTests(ConversationEngineFixture fixture)
{
    private const long NotifyCursorsVersion = 202611140001;
    private const string Uploads = "conversation_attachment_uploads";
    private static readonly System.Reflection.Assembly MigrationAssembly = typeof(ConversationSchemaMigration).Assembly;

    private void MigrateUpTo(string connectionString, long version)
    {
        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                fixture.MigrationAdapter.ConfigureRunner(rb);
                rb.WithGlobalConnectionString(connectionString).ScanIn(MigrationAssembly).For.Migrations();
            })
            .AddSingleton<FluentMigrator.Runner.VersionTableInfo.IVersionTableMetaData>(new ThemiaVersionTable(MigrationAssembly))
            .BuildServiceProvider(false);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp(version);
    }

    private void MigrateFully(string connectionString) =>
        ThemiaMigrations.Run(fixture.MigrationAdapter, connectionString, MigrationAssembly);

    private Task<bool> IndexExistsAsync(string connectionString, string name) =>
        ConversationEngineFixture.ScalarOnAsync<bool>(fixture.Connect(connectionString), fixture.IndexExistsSql, new { Name = name });

    private async Task AssertSchemaCompleteAsync(string connectionString)
    {
        Assert.True(await IndexExistsAsync(connectionString, "ix_conversation_attachment_uploads_created"));
        Assert.True(await IndexExistsAsync(connectionString, "ix_conversation_attachment_uploads_conversation"));
        if (fixture.CollationSql is null)
        {
            return;
        }

        foreach (var column in new[] { "tenant_id", "storage_key" })
        {
            var collation = await ConversationEngineFixture.ScalarOnAsync<string?>(fixture.Connect(connectionString), fixture.CollationSql, new { t = Uploads, c = column });
            Assert.Contains("bin", collation, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Migration_ShouldUpgradeADatabaseWithAttachments_WhenItWasOnThePreviousVersion()
    {
        var connectionString = await fixture.CreateFreshDatabaseAsync();
        MigrateUpTo(connectionString, NotifyCursorsVersion);
        var tenant = "t-" + Guid.NewGuid().ToString("N");
        var (conversation, participant, message) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var now = new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Utc);
        await ConversationEngineFixture.ExecuteOnAsync(fixture.Connect(connectionString),
            $"INSERT INTO conversations (id, tenant_id, subject_type, subject_id, state, retention_state, last_seq, created_at) VALUES ('{conversation}', '{tenant}', 'booking', 'B-1', 0, 0, 1, '{now:yyyy-MM-dd HH:mm:ss}');");
        await ConversationEngineFixture.ExecuteOnAsync(fixture.Connect(connectionString),
            $"INSERT INTO conversation_participants (id, tenant_id, conversation_id, kind, display_name, joined_at, last_read_seq) VALUES ('{participant}', '{tenant}', '{conversation}', 0, 'x', '{now:yyyy-MM-dd HH:mm:ss}', 0);");
        await ConversationEngineFixture.ExecuteOnAsync(fixture.Connect(connectionString),
            $"INSERT INTO conversation_messages (id, tenant_id, conversation_id, seq, participant_id, kind, body, client_message_id, created_at) VALUES ('{message}', '{tenant}', '{conversation}', 1, '{participant}', 0, 'hi', 'm-1', '{now:yyyy-MM-dd HH:mm:ss}');");
        await ConversationEngineFixture.ExecuteOnAsync(fixture.Connect(connectionString),
            $"INSERT INTO conversation_attachments (id, tenant_id, conversation_id, message_id, owner_participant_id, storage_key, content_type, size, sha256, created_at) VALUES ('{Guid.NewGuid()}', '{tenant}', '{conversation}', '{message}', '{participant}', 'k', 'image/png', 1, 'aa', '{now:yyyy-MM-dd HH:mm:ss}');");

        MigrateFully(connectionString);

        var fileName = await ConversationEngineFixture.ScalarOnAsync<string>(fixture.Connect(connectionString), "SELECT file_name FROM conversation_attachments;");
        Assert.Equal(string.Empty, fileName);
        await AssertSchemaCompleteAsync(connectionString);
        MigrateFully(connectionString); // replay
    }

    [Fact]
    public async Task Migration_ShouldFinishTheJob_WhenAPreviousRunDiedAfterCreatingTheTable()
    {
        var connectionString = await fixture.CreateFreshDatabaseAsync();
        MigrateFully(connectionString);

        // The state a run killed between CREATE TABLE and the last index leaves on an engine with non-transactional DDL, with the ledger not recording it.
        foreach (var sql in fixture.PartialStateSql())
        {
            await ConversationEngineFixture.ExecuteOnAsync(fixture.Connect(connectionString), sql);
        }

        var ledger = new ThemiaVersionTable(MigrationAssembly);
        await ConversationEngineFixture.ExecuteOnAsync(fixture.Connect(connectionString),
            $"DELETE FROM {fixture.Quote(ledger.TableName)} WHERE {fixture.Quote(ledger.ColumnName)} = 202611150001;");

        MigrateFully(connectionString);

        await AssertSchemaCompleteAsync(connectionString);
    }
}

[Collection(PostgresConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresConversationAttachmentUploadsUpgradeTests(PostgresConversationFixture fixture) : ConversationAttachmentUploadsUpgradeTests(fixture);

[Collection(MySqlConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlConversationAttachmentUploadsUpgradeTests(MySqlConversationFixture fixture) : ConversationAttachmentUploadsUpgradeTests(fixture);

[Collection(SqlServerConversationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerConversationAttachmentUploadsUpgradeTests(SqlServerConversationFixture fixture) : ConversationAttachmentUploadsUpgradeTests(fixture);
