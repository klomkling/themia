using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// Proves <see cref="ChallengePurgeService"/>'s retention rules and, above all, the gate-advances-only-
/// on-success behaviour: the lesson carried over from <c>Themia.Messaging</c>'s <c>OutboxDrainer</c>,
/// where an earlier version advanced the purge-due gate before confirming success and a single
/// transient failure silently suppressed retention for a whole interval. See the type's remarks.
/// </summary>
public sealed class ChallengePurgeServiceTests : IDisposable
{
    private readonly SqliteConnection keepAlive;
    private readonly string connString;

    public ChallengePurgeServiceTests()
    {
        connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        keepAlive.Execute(SqliteChallengeDialect.CreateTablesSql);
    }

    public void Dispose() => keepAlive.Dispose();

    private void InsertChallenge(Guid id, DateTimeOffset expiresAt) =>
        keepAlive.Execute(
            """
            INSERT INTO challenges (id, tenant_id, key, purpose, secret_hash, secret_salt, token_hash, attempts, expires_at, created_at)
            VALUES (@Id, NULL, 'key', 'login', 'hash', 'salt', NULL, 0, @ExpiresAt, @ExpiresAt);
            """,
            new { Id = id.ToString(), ExpiresAt = expiresAt.ToString("O") });

    private void InsertWindow(Guid id, DateTimeOffset windowStart) =>
        keepAlive.Execute(
            """
            INSERT INTO challenge_rate_windows (id, tenant_id, key, purpose, window_start, count)
            VALUES (@Id, NULL, 'key', NULL, @WindowStart, 1);
            """,
            new { Id = id.ToString(), WindowStart = windowStart.ToString("O") });

    private int ChallengeCount() => keepAlive.ExecuteScalar<int>("SELECT COUNT(*) FROM challenges");

    private int WindowCount() => keepAlive.ExecuteScalar<int>("SELECT COUNT(*) FROM challenge_rate_windows");

    // ---- Expired-challenge purge ---------------------------------------------------------------

    [Fact]
    public async Task PurgeIfDueAsync_ShouldDeleteChallenges_OlderThanRetention()
    {
        var now = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        InsertChallenge(Guid.NewGuid(), now.AddHours(-25)); // older than 24h retention: purged
        InsertChallenge(Guid.NewGuid(), now.AddHours(-23)); // within 24h retention: survives

        var options = new ChallengeOptions { ChallengeRetentionHours = 24 };
        var dialect = new RecordingChallengeDialect(connString);
        var service = new ChallengePurgeService(options, new FakeTimeProvider(now), NullLogger<ChallengePurgeService>.Instance, dialect);

        await service.PurgeIfDueAsync(CancellationToken.None);

        Assert.Equal(1, ChallengeCount());
    }

    // ---- Rate-window purge: must outlive the challenges it counted ----------------------------

    // The core requirement this task exists for: challenges purge on ChallengeRetentionHours, but rate
    // windows purge strictly on their own elapsed-window rule. A window row well past the (deliberately
    // short) ChallengeRetentionHours here must still survive, because it has not yet fully elapsed under
    // the widest configured window plus safety margin. Purging it early would hand an attacker a free
    // reset of the per-key ceiling that bounds the SMS bill.
    [Fact]
    public async Task PurgeIfDueAsync_ShouldNotPurgeRateWindow_BeforeItHasFullyElapsed()
    {
        var now = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        var stillWithinWindow = Guid.NewGuid();
        var fullyElapsed = Guid.NewGuid();
        InsertWindow(stillWithinWindow, now.AddMinutes(-30)); // well within the cutoff: not yet elapsed
        InsertWindow(fullyElapsed, now.AddDays(-2)); // long past the cutoff: elapsed

        // widest window = the store's 1h per-key window; cutoff = now - 1h - 1h margin = now - 2h
        var options = new ChallengeOptions
        {
            ChallengeRetentionHours = 1, // deliberately shorter than the window cutoff
            PerKeyWindow = (20, TimeSpan.FromHours(1)),
        };
        options.ConfigurePurpose("login", p => p.PerScopeWindow = (3, TimeSpan.FromMinutes(15)));

        var dialect = new RecordingChallengeDialect(connString);
        var service = new ChallengePurgeService(options, new FakeTimeProvider(now), NullLogger<ChallengePurgeService>.Instance, dialect);

        await service.PurgeIfDueAsync(CancellationToken.None);

        Assert.Equal(1, WindowCount());
        Assert.Equal(1, keepAlive.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM challenge_rate_windows WHERE id = @Id", new { Id = stillWithinWindow.ToString() }));
    }

    [Fact]
    public async Task PurgeIfDueAsync_ShouldNotPurgeRateWindows_WhenNoPurposeIsConfigured()
    {
        var now = DateTimeOffset.Parse("2026-08-04T00:00:00Z");
        InsertWindow(Guid.NewGuid(), now.AddYears(-1)); // ancient, but nothing to compute a safe cutoff from

        var options = new ChallengeOptions(); // no purposes configured -> WidestConfiguredWindow() == 0
        var dialect = new RecordingChallengeDialect(connString);
        var service = new ChallengePurgeService(options, new FakeTimeProvider(now), NullLogger<ChallengePurgeService>.Instance, dialect);

        await service.PurgeIfDueAsync(CancellationToken.None);

        Assert.Equal(1, WindowCount());
    }

    // ---- Purge gating ---------------------------------------------------------------------------

    [Fact]
    public async Task PurgeIfDueAsync_ShouldNotTouchTheDialect_WhenPurgeDisabled()
    {
        var options = new ChallengeOptions { PurgeEnabled = false };
        var dialect = new RecordingChallengeDialect(connString);
        var service = new ChallengePurgeService(options, TimeProvider.System, NullLogger<ChallengePurgeService>.Instance, dialect);

        await service.PurgeIfDueAsync(CancellationToken.None);

        Assert.Equal(0, dialect.CreateConnectionCalls);
    }

    [Fact]
    public async Task PurgeIfDueAsync_ShouldNotThrow_WhenNoDialectIsRegistered()
    {
        var options = new ChallengeOptions { PurgeEnabled = true };
        var service = new ChallengePurgeService(options, TimeProvider.System, NullLogger<ChallengePurgeService>.Instance, dialect: null);

        var exception = await Record.ExceptionAsync(() => service.PurgeIfDueAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task PurgeIfDueAsync_ShouldNotPurgeTwice_WithinTheInterval()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-04T00:00:00Z"));
        var options = new ChallengeOptions { PurgeEnabled = true };
        var dialect = new RecordingChallengeDialect(connString);
        var service = new ChallengePurgeService(options, time, NullLogger<ChallengePurgeService>.Instance, dialect);

        await service.PurgeIfDueAsync(CancellationToken.None);
        await service.PurgeIfDueAsync(CancellationToken.None);

        Assert.Equal(1, dialect.CreateConnectionCalls);
    }

    // This is the point of Step 2: a failed purge must retry rather than advance the gate. Before this
    // fix (the OutboxDrainer lesson), advancing the gate unconditionally would make the second call below
    // a no-op, and retention would then stay silently broken until a full PurgeInterval elapsed.
    [Fact]
    public async Task PurgeIfDueAsync_ShouldRetryImmediately_WhenThePurgeFails_InsteadOfAdvancingTheGate()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-04T00:00:00Z")); // clock never advances in this test
        var options = new ChallengeOptions { PurgeEnabled = true };
        var dialect = new RecordingChallengeDialect(connString) { ShouldFail = true };
        var service = new ChallengePurgeService(options, time, NullLogger<ChallengePurgeService>.Instance, dialect);

        var firstAttempt = await Record.ExceptionAsync(() => service.PurgeIfDueAsync(CancellationToken.None));
        Assert.Null(firstAttempt); // the failure must not propagate out of the purge service
        Assert.Equal(1, dialect.CreateConnectionCalls);

        // Same instant, still failing: the gate must not have advanced, so this must attempt again.
        var secondAttempt = await Record.ExceptionAsync(() => service.PurgeIfDueAsync(CancellationToken.None));
        Assert.Null(secondAttempt);
        Assert.Equal(2, dialect.CreateConnectionCalls);

        // Now let it succeed, still at the same instant: this attempt must run (proving the two failures
        // never silently satisfied the gate) and must itself advance the gate on success.
        dialect.ShouldFail = false;
        await service.PurgeIfDueAsync(CancellationToken.None);
        Assert.Equal(3, dialect.CreateConnectionCalls);

        // Same instant again: the gate is now set from the successful attempt above, so this must be a
        // no-op — proving success (unlike failure) does advance the gate.
        await service.PurgeIfDueAsync(CancellationToken.None);
        Assert.Equal(3, dialect.CreateConnectionCalls);
    }

    /// <summary>
    /// Wraps <see cref="SqliteChallengeDialect"/> to count <see cref="CreateConnection"/> calls (proving
    /// gating decisions without inspecting private state) and to optionally fail every connection open,
    /// simulating a transient DB failure (timeout, connection refused) at the point
    /// <see cref="ChallengePurgeService"/> first touches the database each cycle.
    /// </summary>
    private sealed class RecordingChallengeDialect(string connectionString) : IChallengeDialect
    {
        private readonly SqliteChallengeDialect inner = new(connectionString);

        public int CreateConnectionCalls { get; private set; }

        public bool ShouldFail { get; set; }

        public DbConnection CreateConnection()
        {
            CreateConnectionCalls++;
            return ShouldFail ? new ThrowingConnection() : inner.CreateConnection();
        }

        public string InsertSql => inner.InsertSql;
        public string SelectLiveByScopeSql => inner.SelectLiveByScopeSql;
        public string SelectLiveByTokenHashSql => inner.SelectLiveByTokenHashSql;
        public string SelectMostRecentByScopeSql => inner.SelectMostRecentByScopeSql;

        /// <inheritdoc />
        public string SelectByIdSql => inner.SelectByIdSql;

        /// <inheritdoc />
        public string MarkRefundedSql => inner.MarkRefundedSql;
        public string ConsumeSql => inner.ConsumeSql;
        public string RecordAttemptSql => inner.RecordAttemptSql;
        public string InvalidateLiveForScopeSql => inner.InvalidateLiveForScopeSql;
        public string PurgeExpiredSql => inner.PurgeExpiredSql;
        public string IncrementWindowSql => inner.IncrementWindowSql;
        public string DecrementWindowSql => inner.DecrementWindowSql;
        public string PurgeElapsedWindowsSql => inner.PurgeElapsedWindowsSql;
    }

    /// <summary>A connection whose <c>Open</c>/<c>OpenAsync</c> always throws, simulating a transient
    /// connectivity failure before any SQL is ever sent.</summary>
    private sealed class ThrowingConnection : DbConnection
    {
        // [AllowNull] matches DbConnection's own nullability annotation on the setter.
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
        }

        public override void Open() => throw new InvalidOperationException("Simulated transient purge failure.");

        public override Task OpenAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Simulated transient purge failure.");

        protected override DbTransaction BeginDbTransaction(IsolationLevel il) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
