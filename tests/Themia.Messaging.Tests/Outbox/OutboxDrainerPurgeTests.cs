using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Themia.Messaging.Outbox;

using Xunit;

namespace Themia.Messaging.Tests.Outbox;

public class OutboxDrainerPurgeTests
{
    private sealed record Row(Guid Id, int Attempts) : IClaimedRow;

    // A dialect that claims nothing, so DrainOnceAsync exercises only the purge decision.
    private sealed class EmptyDialect : IOutboxDialect<Row>
    {
        public DbConnection CreateConnection() => new FakeConnection();

        public Task<IReadOnlyList<Row>> ClaimAsync(
            DbConnection connection, string leaseOwner, DateTimeOffset now,
            DateTimeOffset leaseExpiresAt, int batchSize, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Row>>([]);

        public Task CompleteAsync(DbConnection c, Guid id, DateTimeOffset at, CancellationToken ct)
            => Task.CompletedTask;

        public Task FailAsync(DbConnection c, Guid id, int attempts, DateTimeOffset next,
            bool dead, string error, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingPurgeDialect : IOutboxPurgeDialect<Row>
    {
        public int SentCalls { get; private set; }
        public int DeadCalls { get; private set; }
        public DateTimeOffset LastSentOlderThan { get; private set; }
        public DateTimeOffset LastDeadOlderThan { get; private set; }

        public Task<int> PurgeSentAsync(DbConnection c, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        {
            SentCalls++;
            LastSentOlderThan = olderThan;
            return Task.FromResult(0); // nothing left to delete — loop terminates
        }

        public Task<int> PurgeDeadAsync(DbConnection c, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        {
            DeadCalls++;
            LastDeadOlderThan = olderThan;
            return Task.FromResult(0);
        }
    }

    // Always throws from PurgeSentAsync, simulating a transient DB failure (timeout, lock conflict).
    // Used to pin that a failed purge pass does not advance the interval gate.
    private sealed class ThrowingSentPurgeDialect : IOutboxPurgeDialect<Row>
    {
        public int SentCalls { get; private set; }

        public Task<int> PurgeSentAsync(DbConnection c, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
        {
            SentCalls++;
            throw new InvalidOperationException("Simulated transient purge failure.");
        }

        public Task<int> PurgeDeadAsync(DbConnection c, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
            => Task.FromResult(0);
    }

    // Returns a full batch on the first call and an empty one on the second, so the caller must loop
    // twice to drain it — pins that PurgeAllAsync's "loop until a short batch comes back" behavior exists.
    private sealed class BatchingPurgeDialect(int batchSize) : IOutboxPurgeDialect<Row>
    {
        public int SentCalls { get; private set; }

        public Task<int> PurgeSentAsync(DbConnection c, DateTimeOffset olderThan, int requestedBatchSize, CancellationToken ct)
        {
            SentCalls++;
            return Task.FromResult(SentCalls == 1 ? batchSize : 0);
        }

        public Task<int> PurgeDeadAsync(DbConnection c, DateTimeOffset olderThan, int batchSize, CancellationToken ct)
            => Task.FromResult(0);
    }

    private sealed class NoopDispatcher : IOutboxDispatcher<Row>
    {
        public Task<DispatchResult> DispatchAsync(IServiceProvider sp, Row row, CancellationToken ct)
            => Task.FromResult(DispatchResult.Delivered());
    }

    private static OutboxDrainer<Row> Build(
        OutboxDrainerOptions<Row> options, IOutboxPurgeDialect<Row>? purge, TimeProvider time)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new OutboxDrainer<Row>(
            new EmptyDialect(),
            new NoopDispatcher(),
            new DrainSignal<Row>(),
            services.GetRequiredService<IServiceScopeFactory>(),
            options,
            time,
            NullLogger<OutboxDrainer<Row>>.Instance,
            purge);
    }

    [Fact]
    public async Task DrainOnce_ShouldNotPurge_WhenPurgeDisabled()
    {
        var purge = new RecordingPurgeDialect();
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = false };

        var drainer = Build(options, purge, TimeProvider.System);
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(0, purge.SentCalls);
        Assert.Equal(0, purge.DeadCalls);
    }

    [Fact]
    public async Task DrainOnce_ShouldPurge_OnFirstCycle_WhenEnabled()
    {
        var purge = new RecordingPurgeDialect();
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = true };

        var drainer = Build(options, purge, TimeProvider.System);
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, purge.SentCalls);
        Assert.Equal(1, purge.DeadCalls);
    }

    // The purge is interval-gated: a second cycle moments later must not re-run it.
    [Fact]
    public async Task DrainOnce_ShouldNotPurgeTwice_WithinTheInterval()
    {
        var purge = new RecordingPurgeDialect();
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = true, PurgeIntervalHours = 24 };

        var drainer = Build(options, purge, TimeProvider.System);
        await drainer.DrainOnceAsync(CancellationToken.None);
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, purge.SentCalls);
    }

    [Fact]
    public async Task DrainOnce_ShouldNotThrow_WhenNoPurgeDialectRegistered()
    {
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = true };

        var drainer = Build(options, purge: null, TimeProvider.System);
        var exception = await Record.ExceptionAsync(() => drainer.DrainOnceAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    // Retention is expressed in days and must be subtracted from the drainer's clock, not DateTime.UtcNow.
    // Sent and dead use deliberately different values so a copy-paste swap of the two retention
    // settings in PurgeIfDueAsync would fail this test instead of passing unnoticed.
    [Fact]
    public async Task DrainOnce_ShouldComputeCutoffs_FromRetentionDays()
    {
        var now = new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);
        var purge = new RecordingPurgeDialect();
        var options = new OutboxDrainerOptions<Row>
        {
            PurgeEnabled = true,
            SentRetentionDays = 7,
            DeadRetentionDays = 90,
        };

        var drainer = Build(options, purge, new FixedTimeProvider(now));
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(now.AddDays(-7), purge.LastSentOlderThan);
        Assert.Equal(now.AddDays(-90), purge.LastDeadOlderThan);
    }

    // A purge pass that throws (transient DB timeout, lock conflict) must not suppress the next
    // attempt for a full PurgeIntervalHours — the interval gate may only advance after both purges
    // complete successfully.
    [Fact]
    public async Task DrainOnce_ShouldRetryPurge_OnNextCycle_WhenPurgeFails()
    {
        var purge = new ThrowingSentPurgeDialect();
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = true, PurgeIntervalHours = 24 };

        var drainer = Build(options, purge, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => drainer.DrainOnceAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => drainer.DrainOnceAsync(CancellationToken.None));

        Assert.Equal(2, purge.SentCalls);
    }

    // PurgeAllAsync must keep calling the dialect until a batch comes back short of the requested
    // size — an unbounded single DELETE is the exact failure mode retention batching exists to avoid.
    [Fact]
    public async Task DrainOnce_ShouldKeepPurging_UntilABatchComesBackShort()
    {
        const int batchSize = 3;
        var purge = new BatchingPurgeDialect(batchSize);
        var options = new OutboxDrainerOptions<Row> { PurgeEnabled = true, PurgeBatchSize = batchSize };

        var drainer = Build(options, purge, TimeProvider.System);
        await drainer.DrainOnceAsync(CancellationToken.None);

        Assert.Equal(2, purge.SentCalls);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Minimal DbConnection stand-in: the drainer opens it but the fake dialects never use it.
    private sealed class FakeConnection : DbConnection
    {
        // [AllowNull] matches DbConnection's own nullability annotation on the setter (it accepts null
        // to reset the connection string); without it the override mismatches the base member (CS8765).
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel il) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
