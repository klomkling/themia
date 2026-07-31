using System.Data.Common;

using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Messaging.Inbox;
using Themia.Messaging.MySql;
using Themia.Messaging.Outbox;
using Themia.Messaging.PostgreSql;
using Themia.Messaging.SqlServer;
using Themia.Modules.Messaging.Migrations;

using Xunit;

namespace Themia.Modules.Messaging.IntegrationTests;

/// <summary>
/// Proves the per-engine outbox claim, retention purge, and inbox admission dialects are correct: two
/// drainers on separate connections never double-claim a row, future-scheduled rows are skipped, a
/// stale-lease sending row is reclaimed, batched purge respects its batch size, and admission dedups
/// atomically under concurrency. One concrete class per engine (Postgres / SQL Server / MySQL) — mirrors
/// Themia.Modules.Notifications.IntegrationTests.OutboxClaimConcurrencyTests. Each dialect is resolved
/// through its package's <c>AddThemiaMessaging*</c> DI registration rather than constructed directly, so
/// this exercises the exact wiring an adopter uses.
/// </summary>
public abstract class MessagingDialectTests
{
    private const string TestOrigin = "test-origin";
    private const string TestType = "test.message.v1";
    private const int PendingRows = 40;

    /// <summary>The engine-specific claim dialect under test.</summary>
    protected abstract IOutboxDialect<ClaimedMessageRow> Dialect { get; }

    /// <summary>The engine-specific outbox purge dialect under test.</summary>
    protected abstract IOutboxPurgeDialect<ClaimedMessageRow> PurgeDialect { get; }

    /// <summary>The engine-specific inbox purge dialect under test.</summary>
    protected abstract IInboxPurgeDialect InboxPurgeDialect { get; }

    /// <summary>The engine-specific inbox admission dialect under test.</summary>
    protected abstract IInboxAdmissionDialect AdmissionDialect { get; }

    /// <summary>The unqualified or schema-qualified outbox table identifier for direct inserts on this engine.</summary>
    protected abstract string OutboxTable { get; }

    /// <summary>The unqualified or schema-qualified inbox table identifier for direct inserts on this engine.</summary>
    protected abstract string InboxTable { get; }

    // ---- Claim ----

    [Fact]
    public async Task Concurrent_claims_never_double_claim_a_row()
    {
        await SeedPendingAsync(PendingRows);

        var owner1 = "drainer-1";
        var owner2 = "drainer-2";
        var now = DateTimeOffset.UtcNow;
        var leaseExpiry = now.AddMinutes(2);

        // Each drainer claims on a SEPARATE connection. Whether they run truly simultaneously or one
        // finishes first, the claim must never hand the same row to both — that is the correctness
        // property skip-locked / read-past semantics guarantee. Running several rounds raises the odds
        // of catching a real double-claim race.
        const int rounds = 8;
        for (var round = 0; round < rounds; round++)
        {
            await using var conn1 = Dialect.CreateConnection();
            await using var conn2 = Dialect.CreateConnection();
            await conn1.OpenAsync();
            await conn2.OpenAsync();

            var claim1Task = Dialect.ClaimAsync(conn1, owner1, now, leaseExpiry, PendingRows, default);
            var claim2Task = Dialect.ClaimAsync(conn2, owner2, now, leaseExpiry, PendingRows, default);
            var results = await Task.WhenAll(claim1Task, claim2Task);

            var ids1 = results[0].Select(r => r.Id).ToHashSet();
            var ids2 = results[1].Select(r => r.Id).ToHashSet();

            Assert.Empty(ids1.Intersect(ids2));
            Assert.True(ids1.Count + ids2.Count <= PendingRows,
                $"claimed {ids1.Count + ids2.Count} > {PendingRows} available — double-claim detected");

            await ResetClaimedToPendingAsync(now);
        }
    }

    [Fact]
    public async Task Future_scheduled_row_is_not_claimed()
    {
        var now = DateTimeOffset.UtcNow;
        var futureId = Guid.NewGuid();
        var dueId = Guid.NewGuid();
        await InsertPendingRowAsync(futureId, Guid.CreateVersion7(), nextAttemptAt: now, scheduledFor: now.AddHours(1));
        await InsertPendingRowAsync(dueId, Guid.CreateVersion7(), nextAttemptAt: now, scheduledFor: null);

        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var claimed = await Dialect.ClaimAsync(conn, "drainer", now, now.AddMinutes(2), 10, default);

        var claimedIds = claimed.Select(r => r.Id).ToHashSet();
        Assert.Contains(dueId, claimedIds);
        Assert.DoesNotContain(futureId, claimedIds);
    }

    [Fact]
    public async Task Stale_lease_sending_row_is_reclaimed()
    {
        var now = DateTimeOffset.UtcNow;
        var staleId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        await InsertSendingRowAsync(staleId, Guid.CreateVersion7(), leaseOwner: "dead-drainer", leaseExpiresAt: now.AddMinutes(-1));
        await InsertSendingRowAsync(freshId, Guid.CreateVersion7(), leaseOwner: "live-drainer", leaseExpiresAt: now.AddMinutes(5));

        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var claimed = await Dialect.ClaimAsync(conn, "drainer", now, now.AddMinutes(2), 10, default);

        var claimedIds = claimed.Select(r => r.Id).ToHashSet();
        Assert.Contains(staleId, claimedIds);
        Assert.DoesNotContain(freshId, claimedIds);
    }

    [Fact]
    public async Task Claimed_row_maps_all_fields_correctly()
    {
        // Distinct, recognizable values per column catch a column/property transposition in the dialect.
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var messageId = Guid.CreateVersion7();
        await InsertPendingRowAsync(
            id, messageId, nextAttemptAt: now, scheduledFor: null,
            tenantId: "tenant-x", type: "type-x", payload: "{\"x\":1}", destination: "dest-x",
            entityKey: "entity-x", version: 7, headers: """{"x-trace":"abc"}""");

        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var claimed = await Dialect.ClaimAsync(conn, "drainer", now, now.AddMinutes(2), 10, default);

        var row = Assert.Single(claimed, r => r.Id == id);
        Assert.Equal(messageId, row.MessageId);
        Assert.Equal("tenant-x", row.TenantId);
        Assert.Equal("type-x", row.Type);
        Assert.Equal("{\"x\":1}", row.Payload);
        Assert.Equal("dest-x", row.Destination);
        Assert.Equal(TestOrigin, row.Origin);
        Assert.Equal("entity-x", row.EntityKey);
        Assert.Equal(7, row.Version);
        // F3: headers are selected by the claim query, not just stored — a round-tripped row must carry them.
        Assert.Equal("""{"x-trace":"abc"}""", row.Headers);
        Assert.Equal(0, row.Attempts);
    }

    // ---- Purge ----

    [Fact]
    public async Task PurgeSentAsync_deletes_old_sent_rows_in_batches()
    {
        var now = DateTimeOffset.UtcNow;
        const int purgeBatchSize = 3;
        const int rowCount = purgeBatchSize + 5;

        for (var i = 0; i < rowCount; i++)
        {
            await InsertSentRowAsync(Guid.NewGuid(), Guid.CreateVersion7(), sentAt: now.AddDays(-10));
        }
        var recentId = Guid.NewGuid();
        await InsertSentRowAsync(recentId, Guid.CreateVersion7(), sentAt: now.AddDays(-1));

        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var cutoff = now.AddDays(-2);

        // A single call must be bounded by the requested batch size — the whole point of the per-engine
        // LIMIT / TOP(@batch) syntax the drain loop relies on.
        var firstBatch = await PurgeDialect.PurgeSentAsync(conn, cutoff, purgeBatchSize, default);
        Assert.Equal(purgeBatchSize, firstBatch);
        Assert.Equal(rowCount - purgeBatchSize + 1, await CountRowsAsync(conn, OutboxTable));

        // Loop until nothing eligible remains.
        int deleted;
        do
        {
            deleted = await PurgeDialect.PurgeSentAsync(conn, cutoff, purgeBatchSize, default);
        } while (deleted > 0);

        Assert.Equal(1, await CountRowsAsync(conn, OutboxTable)); // only the recent row survives
        Assert.True(await RowExistsAsync(conn, OutboxTable, recentId));
    }

    [Fact]
    public async Task PurgeDeadAsync_deletes_old_dead_rows_and_leaves_recent_ones()
    {
        var now = DateTimeOffset.UtcNow;
        var oldId = Guid.NewGuid();
        var recentId = Guid.NewGuid();
        await InsertDeadRowAsync(oldId, Guid.CreateVersion7(), nextAttemptAt: now.AddDays(-100));
        await InsertDeadRowAsync(recentId, Guid.CreateVersion7(), nextAttemptAt: now.AddDays(-1));

        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var deleted = await PurgeDialect.PurgeDeadAsync(conn, now.AddDays(-7), 100, default);

        Assert.Equal(1, deleted);
        Assert.False(await RowExistsAsync(conn, OutboxTable, oldId));
        Assert.True(await RowExistsAsync(conn, OutboxTable, recentId));
    }

    [Fact]
    public async Task PurgeAdmittedAsync_deletes_old_inbox_rows_and_leaves_recent_ones()
    {
        var now = DateTimeOffset.UtcNow;
        var oldMessageId = Guid.CreateVersion7();
        var recentMessageId = Guid.CreateVersion7();
        await InsertInboxRowAsync("peer-old", oldMessageId, now.AddDays(-100));
        await InsertInboxRowAsync("peer-recent", recentMessageId, now.AddDays(-1));

        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var deleted = await InboxPurgeDialect.PurgeAdmittedAsync(conn, now.AddDays(-7), 100, default);

        Assert.Equal(1, deleted);
        Assert.False(await InboxRowExistsAsync(conn, "peer-old", oldMessageId));
        Assert.True(await InboxRowExistsAsync(conn, "peer-recent", recentMessageId));
    }

    // ---- Admission ----

    [Fact]
    public async Task First_admission_is_accepted()
    {
        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var messageId = Guid.CreateVersion7();

        var admitted = await AdmissionDialect.TryAdmitAsync(conn, null, "peer-a", messageId, null, TestType, default);

        Assert.True(admitted);
    }

    [Fact]
    public async Task Second_admission_of_the_same_message_is_a_duplicate()
    {
        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var messageId = Guid.CreateVersion7();

        var first = await AdmissionDialect.TryAdmitAsync(conn, null, "peer-a", messageId, null, TestType, default);
        var second = await AdmissionDialect.TryAdmitAsync(conn, null, "peer-a", messageId, null, TestType, default);

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public async Task Same_message_id_from_a_different_origin_is_accepted()
    {
        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var messageId = Guid.CreateVersion7();

        var fromPeerA = await AdmissionDialect.TryAdmitAsync(conn, null, "peer-a", messageId, null, TestType, default);
        var fromPeerB = await AdmissionDialect.TryAdmitAsync(conn, null, "peer-b", messageId, null, TestType, default);

        Assert.True(fromPeerA);
        Assert.True(fromPeerB);
    }

    [Fact]
    public async Task Concurrent_admissions_of_the_same_message_admit_exactly_once()
    {
        var messageId = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var conn = Dialect.CreateConnection();
            await conn.OpenAsync();
            return await AdmissionDialect.TryAdmitAsync(conn, null, "peer-a", messageId, null, TestType, default);
        }));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(7, results.Count(r => !r));
    }

    [Fact]
    public async Task Received_at_is_set_by_the_database()
    {
        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var messageId = Guid.CreateVersion7();
        var before = DateTimeOffset.UtcNow;

        await AdmissionDialect.TryAdmitAsync(conn, null, "peer-a", messageId, null, TestType, default);

        var receivedAt = await ReadReceivedAtAsync(conn, "peer-a", messageId);
        Assert.True(receivedAt >= before.AddMinutes(-1) && receivedAt <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    // F5: every other admission test above passes transaction: null (autocommit). The production path
    // admits inside the CALLER's ambient transaction — on SQL Server that means the WITH (UPDLOCK,
    // HOLDLOCK) existence-check lock is held until the outer commit/rollback, not released immediately.
    // A rolled-back admission must not leave the message admitted, and the row must become admittable
    // again afterwards — otherwise a genuine redelivery would be silently dropped as a duplicate forever.
    [Fact]
    public async Task Admission_rolled_back_in_the_callers_transaction_can_be_admitted_again()
    {
        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var messageId = Guid.CreateVersion7();

        await using (var tx = await conn.BeginTransactionAsync())
        {
            var admitted = await AdmissionDialect.TryAdmitAsync(conn, tx, "peer-a", messageId, null, TestType, default);
            Assert.True(admitted);

            await tx.RollbackAsync();
        }

        // The rollback must have undone the insert entirely — a fresh, unrelated connection sees nothing.
        await using var verifyConn = Dialect.CreateConnection();
        await verifyConn.OpenAsync();
        Assert.False(await InboxRowExistsAsync(verifyConn, "peer-a", messageId));

        // And the message must be admittable again, exactly as if it had never been attempted.
        var readmitted = await AdmissionDialect.TryAdmitAsync(conn, null, "peer-a", messageId, null, TestType, default);
        Assert.True(readmitted);
    }

    // ---- Helpers ----

    private async Task ResetClaimedToPendingAsync(DateTimeOffset now)
    {
        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            $"UPDATE {OutboxTable} SET status = 0, lease_owner = NULL, lease_expires_at = NULL, next_attempt_at = @now",
            new { now });
    }

    private async Task SeedPendingAsync(int count)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < count; i++)
        {
            await InsertPendingRowAsync(Guid.NewGuid(), Guid.CreateVersion7(), nextAttemptAt: now, scheduledFor: null);
        }
    }

    private async Task InsertPendingRowAsync(
        Guid id, Guid messageId, DateTimeOffset nextAttemptAt, DateTimeOffset? scheduledFor,
        string? tenantId = null, string type = TestType, string payload = "{}", string destination = "peer-a",
        string? entityKey = null, long? version = null, string? headers = null)
        => await InsertOutboxRowAsync(
            id, messageId, tenantId, type, payload, destination, entityKey, version, headers,
            status: 0, attempts: 0, nextAttemptAt, scheduledFor, leaseOwner: null, leaseExpiresAt: null, sentAt: null);

    private async Task InsertSendingRowAsync(Guid id, Guid messageId, string leaseOwner, DateTimeOffset leaseExpiresAt)
        => await InsertOutboxRowAsync(
            id, messageId, null, TestType, "{}", "peer-a", null, null, null,
            status: 1, attempts: 0, nextAttemptAt: DateTimeOffset.UtcNow.AddMinutes(-5), scheduledFor: null,
            leaseOwner, leaseExpiresAt, sentAt: null);

    private async Task InsertSentRowAsync(Guid id, Guid messageId, DateTimeOffset sentAt)
        => await InsertOutboxRowAsync(
            id, messageId, null, TestType, "{}", "peer-a", null, null, null,
            status: 2, attempts: 1, nextAttemptAt: sentAt, scheduledFor: null,
            leaseOwner: null, leaseExpiresAt: null, sentAt);

    private async Task InsertDeadRowAsync(Guid id, Guid messageId, DateTimeOffset nextAttemptAt)
        => await InsertOutboxRowAsync(
            id, messageId, null, TestType, "{}", "peer-a", null, null, null,
            status: 4, attempts: 5, nextAttemptAt, scheduledFor: null,
            leaseOwner: null, leaseExpiresAt: null, sentAt: null);

    private async Task InsertOutboxRowAsync(
        Guid id, Guid messageId, string? tenantId, string type, string payload, string destination,
        string? entityKey, long? version, string? headers, int status, int attempts, DateTimeOffset nextAttemptAt,
        DateTimeOffset? scheduledFor, string? leaseOwner, DateTimeOffset? leaseExpiresAt, DateTimeOffset? sentAt)
    {
        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync($"""
            INSERT INTO {OutboxTable}
            (id, message_id, tenant_id, type, payload, destination, origin, entity_key, version, headers,
             status, attempts, next_attempt_at, scheduled_for, lease_owner, lease_expires_at, created_at, sent_at, last_error)
            VALUES
            (@id, @messageId, @tenantId, @type, @payload, @destination, @origin, @entityKey, @version, @headers,
             @status, @attempts, @nextAttemptAt, @scheduledFor, @leaseOwner, @leaseExpiresAt, @nextAttemptAt, @sentAt, NULL)
            """, new
        {
            id,
            messageId,
            tenantId,
            type,
            payload,
            destination,
            origin = TestOrigin,
            entityKey,
            version,
            headers,
            status,
            attempts,
            nextAttemptAt,
            scheduledFor,
            leaseOwner,
            leaseExpiresAt,
            sentAt,
        });
    }

    private async Task InsertInboxRowAsync(string origin, Guid messageId, DateTimeOffset receivedAt)
    {
        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync($"""
            INSERT INTO {InboxTable} (origin, message_id, tenant_id, type, received_at)
            VALUES (@origin, @messageId, NULL, @type, @receivedAt)
            """, new { origin, messageId, type = TestType, receivedAt });
    }

    private static async Task<int> CountRowsAsync(DbConnection conn, string table)
        => await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table}");

    private static async Task<bool> RowExistsAsync(DbConnection conn, string table, Guid id)
        => await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {table} WHERE id = @id", new { id }) > 0;

    private async Task<bool> InboxRowExistsAsync(DbConnection conn, string origin, Guid messageId)
        => await conn.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM {InboxTable} WHERE origin = @origin AND message_id = @messageId",
            new { origin, messageId }) > 0;

    // The column is DateTimeOffset-typed SQL on Postgres/SQL Server (datetimeoffset/timestamptz) and
    // DATETIME(6) on MySQL. Npgsql and MySqlConnector both hand back a plain DateTime for their
    // respective types; only Microsoft.Data.SqlClient returns a DateTimeOffset directly. Read as object
    // and normalize rather than assuming one driver's mapping.
    private async Task<DateTimeOffset> ReadReceivedAtAsync(DbConnection conn, string origin, Guid messageId)
    {
        var value = await conn.ExecuteScalarAsync(
            $"SELECT received_at FROM {InboxTable} WHERE origin = @origin AND message_id = @messageId",
            new { origin, messageId });

        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException($"Unexpected received_at value type: {value?.GetType()}"),
        };
    }

    /// <summary>Builds a <see cref="ServiceProvider"/> registering just the connection string and the
    /// engine's messaging dialects, mirroring how an adopter registers them.</summary>
    protected static ServiceProvider BuildProvider(string connectionString, Action<IServiceCollection> registerEngine)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        registerEngine(services);

        return services.BuildServiceProvider();
    }
}

/// <summary>Postgres execution of <see cref="MessagingDialectTests"/>.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresMessagingDialectTests : MessagingDialectTests, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private ServiceProvider provider = null!;
    private IOutboxDialect<ClaimedMessageRow> dialect = null!;
    private IOutboxPurgeDialect<ClaimedMessageRow> purgeDialect = null!;
    private IInboxPurgeDialect inboxPurgeDialect = null!;
    private IInboxAdmissionDialect admissionDialect = null!;

    protected override IOutboxDialect<ClaimedMessageRow> Dialect => dialect;
    protected override IOutboxPurgeDialect<ClaimedMessageRow> PurgeDialect => purgeDialect;
    protected override IInboxPurgeDialect InboxPurgeDialect => inboxPurgeDialect;
    protected override IInboxAdmissionDialect AdmissionDialect => admissionDialect;
    protected override string OutboxTable => "messaging.outbox_messages";
    protected override string InboxTable => "messaging.inbox_messages";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var connString = container.GetConnectionString();
        ThemiaMigrations.Run(MigrationEngine.Postgres, connString, typeof(MessagingSchemaMigration).Assembly);

        provider = BuildProvider(connString, s => s.AddThemiaMessagingPostgreSql());
        dialect = provider.GetRequiredService<IOutboxDialect<ClaimedMessageRow>>();
        purgeDialect = provider.GetRequiredService<IOutboxPurgeDialect<ClaimedMessageRow>>();
        inboxPurgeDialect = provider.GetRequiredService<IInboxPurgeDialect>();
        admissionDialect = provider.GetRequiredService<IInboxAdmissionDialect>();
    }

    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await container.DisposeAsync();
    }
}

/// <summary>SQL Server execution of <see cref="MessagingDialectTests"/>.</summary>
[Trait("Category", "Integration")]
public sealed class SqlServerMessagingDialectTests : MessagingDialectTests, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
    private ServiceProvider provider = null!;
    private IOutboxDialect<ClaimedMessageRow> dialect = null!;
    private IOutboxPurgeDialect<ClaimedMessageRow> purgeDialect = null!;
    private IInboxPurgeDialect inboxPurgeDialect = null!;
    private IInboxAdmissionDialect admissionDialect = null!;

    protected override IOutboxDialect<ClaimedMessageRow> Dialect => dialect;
    protected override IOutboxPurgeDialect<ClaimedMessageRow> PurgeDialect => purgeDialect;
    protected override IInboxPurgeDialect InboxPurgeDialect => inboxPurgeDialect;
    protected override IInboxAdmissionDialect AdmissionDialect => admissionDialect;
    protected override string OutboxTable => "[messaging].[outbox_messages]";
    protected override string InboxTable => "[messaging].[inbox_messages]";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var connString = container.GetConnectionString();
        ThemiaMigrations.Run(MigrationEngine.SqlServer, connString, typeof(MessagingSchemaMigration).Assembly);

        provider = BuildProvider(connString, s => s.AddThemiaMessagingSqlServer());
        dialect = provider.GetRequiredService<IOutboxDialect<ClaimedMessageRow>>();
        purgeDialect = provider.GetRequiredService<IOutboxPurgeDialect<ClaimedMessageRow>>();
        inboxPurgeDialect = provider.GetRequiredService<IInboxPurgeDialect>();
        admissionDialect = provider.GetRequiredService<IInboxAdmissionDialect>();
    }

    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await container.DisposeAsync();
    }
}

/// <summary>MySQL execution of <see cref="MessagingDialectTests"/>.</summary>
[Trait("Category", "Integration")]
public sealed class MySqlMessagingDialectTests : MessagingDialectTests, IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();
    private ServiceProvider provider = null!;
    private IOutboxDialect<ClaimedMessageRow> dialect = null!;
    private IOutboxPurgeDialect<ClaimedMessageRow> purgeDialect = null!;
    private IInboxPurgeDialect inboxPurgeDialect = null!;
    private IInboxAdmissionDialect admissionDialect = null!;

    protected override IOutboxDialect<ClaimedMessageRow> Dialect => dialect;
    protected override IOutboxPurgeDialect<ClaimedMessageRow> PurgeDialect => purgeDialect;
    protected override IInboxPurgeDialect InboxPurgeDialect => inboxPurgeDialect;
    protected override IInboxAdmissionDialect AdmissionDialect => admissionDialect;
    protected override string OutboxTable => "outbox_messages";
    protected override string InboxTable => "inbox_messages";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var connString = container.GetConnectionString();
        ThemiaMigrations.Run(MigrationEngine.MySql, connString, typeof(MessagingSchemaMigration).Assembly);

        provider = BuildProvider(connString, s => s.AddThemiaMessagingMySql());
        dialect = provider.GetRequiredService<IOutboxDialect<ClaimedMessageRow>>();
        purgeDialect = provider.GetRequiredService<IOutboxPurgeDialect<ClaimedMessageRow>>();
        inboxPurgeDialect = provider.GetRequiredService<IInboxPurgeDialect>();
        admissionDialect = provider.GetRequiredService<IInboxAdmissionDialect>();
    }

    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await container.DisposeAsync();
    }
}
