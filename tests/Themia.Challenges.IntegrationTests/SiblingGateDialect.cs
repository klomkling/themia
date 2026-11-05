using System.Data.Common;
using System.Threading;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// Wraps a real engine's <see cref="IChallengeDialect"/> to force the interleaving the sibling-void proof needs:
/// both racing verifies have already run <see cref="IChallengeDialect.ConsumeSql"/> when either is let through to
/// <see cref="IChallengeDialect.InvalidateLiveForScopeSql"/>. A bare <c>Task.WhenAll</c> does not do that on SQL
/// Server (one call can finish before the other opens its connection), so a green test would prove nothing.
/// </summary>
/// <remarks>
/// A gate that is not reached by both parties within the timeout fails the test instead of letting the calls run
/// one after the other — a missed barrier must never turn into a sequential, trivially green run. The timeout is
/// generous because a deadlock between the two voids is the normal result of this race and the engine takes a
/// moment to detect it. Counts the voids issued so a test can assert that a lost consume never reaches one.
/// </remarks>
internal sealed class SiblingGateDialect : IChallengeDialect
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(60);

    private readonly IChallengeDialect inner;
    private readonly Barrier? beforeConsume;
    private readonly Barrier? beforeVoid;
    private int voidCalls;

    /// <summary>Wraps <paramref name="inner"/>; either barrier may be null to leave that step ungated.</summary>
    public SiblingGateDialect(IChallengeDialect inner, Barrier? beforeConsume, Barrier? beforeVoid)
    {
        this.inner = inner;
        this.beforeConsume = beforeConsume;
        this.beforeVoid = beforeVoid;
    }

    /// <summary>How many times a void statement was requested.</summary>
    public int VoidCalls => Volatile.Read(ref voidCalls);

    /// <inheritdoc />
    public DbConnection CreateConnection() => inner.CreateConnection();

    /// <inheritdoc />
    public string InsertSql => inner.InsertSql;

    /// <inheritdoc />
    public string SelectLiveByScopeSql(ChallengeTenancy tenancy) => inner.SelectLiveByScopeSql(tenancy);

    /// <inheritdoc />
    public string SelectLiveByTokenHashSql => inner.SelectLiveByTokenHashSql;

    /// <inheritdoc />
    public string SelectMostRecentByScopeSql(ChallengeTenancy tenancy) => inner.SelectMostRecentByScopeSql(tenancy);

    /// <inheritdoc />
    public string SelectByIdSql => inner.SelectByIdSql;

    /// <inheritdoc />
    public string MarkRefundedSql => inner.MarkRefundedSql;

    /// <inheritdoc />
    public string ConsumeSql
    {
        get
        {
            Gate(beforeConsume, "ConsumeSql");
            return inner.ConsumeSql;
        }
    }

    /// <inheritdoc />
    public string RecordAttemptSql => inner.RecordAttemptSql;

    /// <inheritdoc />
    public string InvalidateLiveForScopeSql(ChallengeTenancy tenancy)
    {
        Interlocked.Increment(ref voidCalls);
        Gate(beforeVoid, "InvalidateLiveForScopeSql");
        return inner.InvalidateLiveForScopeSql(tenancy);
    }

    /// <inheritdoc />
    public string PurgeExpiredSql => inner.PurgeExpiredSql;

    /// <inheritdoc />
    public string IncrementWindowSql(RateWindowBucket bucket) => inner.IncrementWindowSql(bucket);

    /// <inheritdoc />
    public string DecrementWindowSql(RateWindowBucket bucket) => inner.DecrementWindowSql(bucket);

    /// <inheritdoc />
    public string PurgeElapsedWindowsSql => inner.PurgeElapsedWindowsSql;

    /// <inheritdoc />
    public bool IsDeadlock(DbException exception) => inner.IsDeadlock(exception);

    private static void Gate(Barrier? barrier, string step)
    {
        // Open once both parties have met: a statement that is re-run (a deadlock retry) must not wait for a partner that is gone.
        if (barrier is not null && barrier.CurrentPhaseNumber == 0 && !barrier.SignalAndWait(GateTimeout))
        {
            throw new InvalidOperationException(Diagnose(barrier, step));
        }
    }

    /// <summary>
    /// What a timed-out rendezvous knows: which step, how many parties had not arrived, and whether the thread pool was
    /// starved (every worker busy and work queued), which is what a blocked gate in async code looks like from outside.
    /// </summary>
    internal static string Diagnose(Barrier barrier, string step)
    {
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        ThreadPool.GetMaxThreads(out var maxWorkers, out _);
        return $"The two racing verifies never both reached {step} within {GateTimeout.TotalSeconds:0} s; the run would have been sequential. "
            + $"Parties not yet arrived: {barrier.ParticipantsRemaining} of {barrier.ParticipantCount}. "
            + $"Thread pool: {ThreadPool.ThreadCount} threads (min {minWorkers}, max {maxWorkers}), {ThreadPool.PendingWorkItemCount} queued work items, "
            + $"{Environment.ProcessorCount} processors.";
    }
}
