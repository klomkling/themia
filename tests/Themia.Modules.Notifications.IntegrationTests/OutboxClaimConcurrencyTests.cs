using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Modules.Notifications.Migrations;
using Themia.Modules.Notifications.MySql;
using Themia.Modules.Notifications.Outbox;
using Themia.Modules.Notifications.PostgreSql;
using Themia.Modules.Notifications.SqlServer;
using Themia.Notifications;
using Xunit;

namespace Themia.Modules.Notifications.IntegrationTests;

/// <summary>
/// Proves the per-engine atomic claim is correct under concurrency: two drainers on separate
/// connections never double-claim a row, future-scheduled rows are skipped, and a stale-lease
/// sending row is reclaimed. One concrete class per engine (Postgres / SQL Server / MySQL).
/// </summary>
public abstract class OutboxClaimConcurrencyTests
{
    private const int PendingRows = 40;

    /// <summary>The engine-specific dialect under test (its own connection factory + claim SQL).</summary>
    protected abstract INotificationsSqlDialect Dialect { get; }

    /// <summary>The unqualified or schema-qualified table identifier for direct inserts on this engine.</summary>
    protected abstract string OutboxTable { get; }

    [Fact]
    public async Task Concurrent_claims_never_double_claim_a_row()
    {
        await SeedPendingAsync(PendingRows);

        var owner1 = "drainer-1";
        var owner2 = "drainer-2";
        var now = DateTimeOffset.UtcNow;
        var leaseExpiry = now.AddMinutes(2);

        // Each drainer asks for the whole table on a SEPARATE connection. Whether they run truly
        // simultaneously or one finishes first, the claim must never hand the same row to both — that
        // is the correctness property skip-locked / read-past guarantees. Running them many times
        // raises the odds of catching a real double-claim race.
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

            // No row is handed to both drainers, and together they claim no more than what exists.
            Assert.Empty(ids1.Intersect(ids2));
            Assert.True(ids1.Count + ids2.Count <= PendingRows,
                $"claimed {ids1.Count + ids2.Count} > {PendingRows} available — double-claim detected");

            // Reset every claimed row back to pending so the next round has the full set to contend for.
            await ResetClaimedToPendingAsync(now);
        }
    }

    [Fact]
    public async Task Future_scheduled_row_is_not_claimed()
    {
        var now = DateTimeOffset.UtcNow;
        var futureId = Guid.NewGuid();
        var dueId = Guid.NewGuid();
        await InsertRowAsync(futureId, status: 0, nextAttemptAt: now, scheduledFor: now.AddHours(1));
        await InsertRowAsync(dueId, status: 0, nextAttemptAt: now, scheduledFor: null);

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
        // A sending row whose lease already expired must be reclaimable.
        await InsertRowAsync(staleId, status: 1, nextAttemptAt: now.AddMinutes(-5), scheduledFor: null,
            leaseOwner: "dead-drainer", leaseExpiresAt: now.AddMinutes(-1));
        // A sending row with a still-valid lease must NOT be reclaimed.
        await InsertRowAsync(freshId, status: 1, nextAttemptAt: now.AddMinutes(-5), scheduledFor: null,
            leaseOwner: "live-drainer", leaseExpiresAt: now.AddMinutes(5));

        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var claimed = await Dialect.ClaimAsync(conn, "drainer", now, now.AddMinutes(2), 10, default);

        var claimedIds = claimed.Select(r => r.Id).ToHashSet();
        Assert.Contains(staleId, claimedIds);
        Assert.DoesNotContain(freshId, claimedIds);
    }

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
            await InsertRowAsync(Guid.NewGuid(), status: 0, nextAttemptAt: now, scheduledFor: null);
        }
    }

    private async Task InsertRowAsync(
        Guid id, int status, DateTimeOffset nextAttemptAt, DateTimeOffset? scheduledFor,
        string? leaseOwner = null, DateTimeOffset? leaseExpiresAt = null)
    {
        await using var conn = Dialect.CreateConnection();
        await conn.OpenAsync();
        var sql = $"""
            INSERT INTO {OutboxTable}
            (id, tenant_id, channel, recipient, subject, body, status, attempts,
             next_attempt_at, scheduled_for, lease_owner, lease_expires_at, created_at, sent_at, last_error)
            VALUES
            (@id, NULL, @channel, @recipient, NULL, @body, @status, 0,
             @next, @scheduled, @owner, @exp, @created, NULL, NULL)
            """;
        await conn.ExecuteAsync(sql, new
        {
            id,
            channel = (int)NotificationChannel.Email,
            recipient = "to@example.com",
            body = "hello",
            status,
            next = nextAttemptAt,
            scheduled = scheduledFor,
            owner = leaseOwner,
            exp = leaseExpiresAt,
            created = nextAttemptAt,
        });
    }
}

/// <summary>Postgres execution of <see cref="OutboxClaimConcurrencyTests"/>.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresOutboxClaimConcurrencyTests : OutboxClaimConcurrencyTests, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private INotificationsSqlDialect dialect = null!;

    protected override INotificationsSqlDialect Dialect => dialect;
    protected override string OutboxTable => "notifications.outbox_messages";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var connString = container.GetConnectionString();
        ThemiaMigrations.Run(MigrationEngine.Postgres, connString, typeof(NotificationsSchemaMigration).Assembly);
        dialect = new PostgresNotificationsDialect(connString);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}

/// <summary>SQL Server execution of <see cref="OutboxClaimConcurrencyTests"/>.</summary>
[Trait("Category", "Integration")]
public sealed class SqlServerOutboxClaimConcurrencyTests : OutboxClaimConcurrencyTests, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();
    private INotificationsSqlDialect dialect = null!;

    protected override INotificationsSqlDialect Dialect => dialect;
    protected override string OutboxTable => "notifications.outbox_messages";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var connString = container.GetConnectionString();
        ThemiaMigrations.Run(MigrationEngine.SqlServer, connString, typeof(NotificationsSchemaMigration).Assembly);
        dialect = new SqlServerNotificationsDialect(connString);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}

/// <summary>MySQL execution of <see cref="OutboxClaimConcurrencyTests"/>.</summary>
[Trait("Category", "Integration")]
public sealed class MySqlOutboxClaimConcurrencyTests : OutboxClaimConcurrencyTests, IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();
    private INotificationsSqlDialect dialect = null!;

    protected override INotificationsSqlDialect Dialect => dialect;
    protected override string OutboxTable => "outbox_messages";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        var connString = container.GetConnectionString();
        ThemiaMigrations.Run(MigrationEngine.MySql, connString, typeof(NotificationsSchemaMigration).Assembly);
        dialect = new MySqlNotificationsDialect(connString);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();
}
