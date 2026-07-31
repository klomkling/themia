using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Messaging.Messages;
using Themia.Messaging.Outbox;
using Themia.Messaging.PostgreSql;
using Themia.Modules.Messaging;
using Themia.Modules.Messaging.Entities;
using Themia.Modules.Messaging.Migrations;
using Themia.Modules.Messaging.Stores;

using Xunit;

namespace Themia.Modules.Messaging.IntegrationTests;

/// <summary>End-to-end outbox drain: enqueue → claim → dispatch → mark sent / dead, plus retention purge
/// and the unique-constraint guard (EF peer, Postgres).</summary>
[Trait("Category", "Integration")]
public sealed class OutboxRoundTripTests : IAsyncLifetime
{
    private const string TestOrigin = "test-origin";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(MessagingSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Drain_delivers_a_pending_message_and_marks_it_sent()
    {
        var dispatcher = new RecordingDispatcher();
        await using var provider = BuildProvider();

        var messageId = await EnqueueMessageAsync(provider, "peer-a");

        var drainer = CreateDrainer(provider, dispatcher, new OutboxDrainerOptions<ClaimedMessageRow> { MaxBatchSize = 10 });
        var drained = await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, drained);
        var delivered = Assert.Single(dispatcher.Delivered);
        Assert.Equal(messageId, delivered.MessageId);

        var (status, attempts, _) = await ReadRowByMessageIdAsync(messageId);
        Assert.Equal((int)OutboxStatus.Sent, status);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Failing_dispatcher_retries_then_dead_letters_after_max_attempts()
    {
        const int maxAttempts = 3;
        var dispatcher = new FailingDispatcher();
        await using var provider = BuildProvider();

        var messageId = await EnqueueMessageAsync(provider, "peer-a");
        var drainer = CreateDrainer(
            provider, dispatcher,
            new OutboxDrainerOptions<ClaimedMessageRow> { MaxAttempts = maxAttempts, MaxBatchSize = 10 });

        // Each failure moves next_attempt_at into the future; force it due so the next drain re-claims it.
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            await SetDueNowByMessageIdAsync(messageId);
            await drainer.DrainOnceAsync(CancellationToken.None);

            var (status, attempts, _) = await ReadRowByMessageIdAsync(messageId);
            Assert.Equal(attempt, attempts);
            var expectedStatus = attempt < maxAttempts ? OutboxStatus.Failed : OutboxStatus.Dead;
            Assert.Equal((int)expectedStatus, status);
        }

        Assert.Equal(maxAttempts, dispatcher.Attempts);
    }

    [Fact]
    public async Task Permanent_failure_dead_letters_immediately_without_retry()
    {
        var dispatcher = new PermanentFailureDispatcher();
        await using var provider = BuildProvider();

        var messageId = await EnqueueMessageAsync(provider, "peer-a");
        var drainer = CreateDrainer(
            provider, dispatcher,
            new OutboxDrainerOptions<ClaimedMessageRow> { MaxAttempts = 5, MaxBatchSize = 10 });

        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, dispatcher.Attempts);
        var (status, attempts, _) = await ReadRowByMessageIdAsync(messageId);
        Assert.Equal((int)OutboxStatus.Dead, status); // permanent — no retry
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Purge_deletes_sent_rows_past_the_window_and_leaves_recent_ones()
    {
        await using var provider = BuildProvider();
        var now = DateTimeOffset.UtcNow;

        var oldId = Guid.NewGuid();
        var recentId = Guid.NewGuid();
        await InsertSentRowAsync(oldId, Guid.CreateVersion7(), "peer-a", sentAt: now.AddDays(-10));
        await InsertSentRowAsync(recentId, Guid.CreateVersion7(), "peer-a", sentAt: now.AddDays(-1));

        var drainer = CreateDrainer(
            provider, new RecordingDispatcher(),
            new OutboxDrainerOptions<ClaimedMessageRow> { PurgeEnabled = true, SentRetentionDays = 7 },
            withPurge: true);

        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.False(await RowExistsAsync(oldId));
        Assert.True(await RowExistsAsync(recentId));
    }

    [Fact]
    public async Task Purge_deletes_in_batches_and_terminates()
    {
        await using var provider = BuildProvider();
        var now = DateTimeOffset.UtcNow;
        const int purgeBatchSize = 3;
        const int rowCount = purgeBatchSize + 5;

        for (var i = 0; i < rowCount; i++)
        {
            await InsertSentRowAsync(Guid.NewGuid(), Guid.CreateVersion7(), "peer-a", sentAt: now.AddDays(-10));
        }

        // A single call must be bounded by the requested batch size, not delete every eligible row at
        // once — this is the LIMIT-via-ctid property that keeps a DELETE's lock hold short on a large
        // table. Without this assertion, a query that dropped the LIMIT would still leave the table
        // empty at the end of one drain cycle and this test would not catch it.
        var dialect = provider.GetRequiredService<IOutboxDialect<ClaimedMessageRow>>();
        var purgeDialect = provider.GetRequiredService<IOutboxPurgeDialect<ClaimedMessageRow>>();
        var cutoff = now.AddDays(-1);
        await using (var connection = dialect.CreateConnection())
        {
            await connection.OpenAsync();
            var firstBatchDeleted = await purgeDialect.PurgeSentAsync(connection, cutoff, purgeBatchSize, CancellationToken.None);
            Assert.Equal(purgeBatchSize, firstBatchDeleted);
        }

        Assert.Equal(rowCount - purgeBatchSize, await CountRowsAsync());

        var drainer = CreateDrainer(
            provider, new RecordingDispatcher(),
            new OutboxDrainerOptions<ClaimedMessageRow>
            {
                PurgeEnabled = true,
                SentRetentionDays = 1,
                PurgeBatchSize = purgeBatchSize,
            },
            withPurge: true);

        // One drain cycle: PurgeAllAsync must loop internally over the remaining rows rather than
        // stopping after one more batch.
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(0, await CountRowsAsync());
    }

    [Fact]
    public async Task Unique_constraint_rejects_the_same_message_id_for_the_same_destination()
    {
        await using var provider = BuildProvider();
        var messageId = Guid.CreateVersion7();

        await EnqueueMessageAsync(provider, "peer-a", messageId);

        await Assert.ThrowsAsync<UniqueConstraintException>(
            () => EnqueueMessageAsync(provider, "peer-a", messageId));
    }

    [Fact]
    public async Task Same_message_id_is_allowed_for_two_different_destinations()
    {
        await using var provider = BuildProvider();
        var messageId = Guid.CreateVersion7();

        await EnqueueMessageAsync(provider, "peer-a", messageId);
        await EnqueueMessageAsync(provider, "peer-b", messageId);

        Assert.Equal(2, await CountByMessageIdAsync(messageId));
    }

    private ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaPostgres<TestMessagingDbContext>(configuration);
        services.AddThemiaDataRepositories<TestMessagingDbContext>();
        services.AddSingleton(new MessagingModuleOptions { ConnectionStringName = "Default", Origin = TestOrigin });
        services.AddScoped<IMessageOutboxStore, MessageOutboxStore>();
        services.AddThemiaMessagingPostgreSql();

        return services.BuildServiceProvider();
    }

    private static OutboxDrainer<ClaimedMessageRow> CreateDrainer(
        ServiceProvider provider,
        IOutboxDispatcher<ClaimedMessageRow> dispatcher,
        OutboxDrainerOptions<ClaimedMessageRow> options,
        bool withPurge = false)
    {
        var dialect = provider.GetRequiredService<IOutboxDialect<ClaimedMessageRow>>();
        var purgeDialect = withPurge ? provider.GetRequiredService<IOutboxPurgeDialect<ClaimedMessageRow>>() : null;
        return new OutboxDrainer<ClaimedMessageRow>(
            dialect,
            dispatcher,
            new DrainSignal<ClaimedMessageRow>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<OutboxDrainer<ClaimedMessageRow>>.Instance,
            purgeDialect);
    }

    private static async Task<Guid> EnqueueMessageAsync(ServiceProvider provider, string destination, Guid? messageId = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMessageOutboxStore>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var id = messageId ?? Guid.CreateVersion7();
        var message = new MessageEnvelope
        {
            MessageId = id,
            Type = "test.message.v1",
            Payload = "{}",
            Destination = destination,
            Origin = TestOrigin,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await store.EnqueueAsync(message, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        return id;
    }

    // After a failure the row's next_attempt_at sits in the future; reset it to now so the next claim is due.
    private async Task SetDueNowByMessageIdAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE messaging.outbox_messages SET next_attempt_at = @now, lease_owner = NULL, lease_expires_at = NULL WHERE message_id = @messageId",
            new { now = DateTimeOffset.UtcNow, messageId });
    }

    private async Task<(int Status, int Attempts, DateTimeOffset NextAttemptAt)> ReadRowByMessageIdAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync();
        var row = await connection.QuerySingleAsync<(int Status, int Attempts, DateTimeOffset NextAttemptAt)>(
            "SELECT status, attempts, next_attempt_at FROM messaging.outbox_messages WHERE message_id = @messageId",
            new { messageId });
        return row;
    }

    // Inserted directly: the store only ever creates pending rows, but retention purge needs pre-existing
    // sent rows outside the drainer's own reach.
    private async Task InsertSentRowAsync(Guid id, Guid messageId, string destination, DateTimeOffset sentAt)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("""
            INSERT INTO messaging.outbox_messages
            (id, message_id, tenant_id, type, payload, destination, origin, entity_key, version, headers,
             status, attempts, next_attempt_at, scheduled_for, lease_owner, lease_expires_at, created_at, sent_at, last_error)
            VALUES
            (@id, @messageId, NULL, 'test.message.v1', '{}', @destination, @origin, NULL, NULL, NULL,
             2, 1, @sentAt, NULL, NULL, NULL, @sentAt, @sentAt, NULL)
            """, new { id, messageId, destination, origin = TestOrigin, sentAt });
    }

    private async Task<bool> RowExistsAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync();
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM messaging.outbox_messages WHERE id = @id", new { id });
        return count > 0;
    }

    private async Task<int> CountRowsAsync()
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM messaging.outbox_messages");
    }

    private async Task<int> CountByMessageIdAsync(Guid messageId)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM messaging.outbox_messages WHERE message_id = @messageId", new { messageId });
    }

    private sealed class RecordingDispatcher : IOutboxDispatcher<ClaimedMessageRow>
    {
        public List<ClaimedMessageRow> Delivered { get; } = [];

        public Task<DispatchResult> DispatchAsync(IServiceProvider sp, ClaimedMessageRow row, CancellationToken ct)
        {
            Delivered.Add(row);
            return Task.FromResult(DispatchResult.Delivered());
        }
    }

    private sealed class FailingDispatcher : IOutboxDispatcher<ClaimedMessageRow>
    {
        public int Attempts { get; private set; }

        public Task<DispatchResult> DispatchAsync(IServiceProvider sp, ClaimedMessageRow row, CancellationToken ct)
        {
            Attempts++;
            return Task.FromResult(DispatchResult.Transient("simulated transient failure"));
        }
    }

    private sealed class PermanentFailureDispatcher : IOutboxDispatcher<ClaimedMessageRow>
    {
        public int Attempts { get; private set; }

        public Task<DispatchResult> DispatchAsync(IServiceProvider sp, ClaimedMessageRow row, CancellationToken ct)
        {
            Attempts++;
            return Task.FromResult(DispatchResult.Permanent("simulated permanent rejection"));
        }
    }
}
