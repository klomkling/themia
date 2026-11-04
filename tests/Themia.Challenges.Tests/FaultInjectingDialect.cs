using System.Data.Common;
using System.Threading;

namespace Themia.Challenges.Tests;

/// <summary>
/// Wraps <see cref="SqliteChallengeDialect"/> to make chosen statements fail on demand, so the deadlock handling and the
/// commit-failure path can be driven without a real engine. A "deadlock" here is any exception whose message contains
/// <see cref="DeadlockMarker"/>; <see cref="Classify"/> decides whether the dialect reports it as one.
/// </summary>
internal sealed class FaultInjectingDialect : IChallengeDialect
{
    /// <summary>Text the injected failing statements carry, matched by <see cref="IsDeadlock"/> when classifying.</summary>
    public const string DeadlockMarker = "deadlock_sim";

    private const string FailingStatement = "INSERT INTO deadlock_sim_missing (x) VALUES (@Id);";

    private readonly SqliteChallengeDialect inner;
    private int failInsertTimes;
    private int failIncrementTimes;
    private int failInvalidateTimes;

    public FaultInjectingDialect(SqliteChallengeDialect inner) => this.inner = inner;

    /// <summary>Whether <see cref="IsDeadlock"/> reports the injected failure as a deadlock.</summary>
    public bool Classify { get; set; } = true;

    /// <summary>The next <paramref name="times"/> insert statements fail.</summary>
    public void FailInsert(int times) => failInsertTimes = times;

    /// <summary>The next <paramref name="times"/> window increments fail.</summary>
    public void FailIncrement(int times) => failIncrementTimes = times;

    /// <summary>The next <paramref name="times"/> scope voids fail.</summary>
    public void FailInvalidate(int times) => failInvalidateTimes = times;

    public DbConnection CreateConnection() => inner.CreateConnection();

    public string InsertSql => TakeFailure(ref failInsertTimes) ? FailingStatement : inner.InsertSql;

    public string SelectLiveByScopeSql(ChallengeTenancy tenancy) => inner.SelectLiveByScopeSql(tenancy);

    public string SelectLiveByTokenHashSql => inner.SelectLiveByTokenHashSql;

    public string SelectMostRecentByScopeSql(ChallengeTenancy tenancy) => inner.SelectMostRecentByScopeSql(tenancy);

    public string SelectByIdSql => inner.SelectByIdSql;

    public string MarkRefundedSql => inner.MarkRefundedSql;

    public string ConsumeSql => inner.ConsumeSql;

    public string RecordAttemptSql => inner.RecordAttemptSql;

    public string InvalidateLiveForScopeSql(ChallengeTenancy tenancy) =>
        TakeFailure(ref failInvalidateTimes) ? "UPDATE deadlock_sim_missing SET x = @ConsumedAt WHERE y = @Key;" : inner.InvalidateLiveForScopeSql(tenancy);

    public string PurgeExpiredSql => inner.PurgeExpiredSql;

    public string IncrementWindowSql(RateWindowBucket bucket) =>
        TakeFailure(ref failIncrementTimes) ? "SELECT * FROM deadlock_sim_missing WHERE x = @Id;" : inner.IncrementWindowSql(bucket);

    public string DecrementWindowSql(RateWindowBucket bucket) => inner.DecrementWindowSql(bucket);

    public string PurgeElapsedWindowsSql => inner.PurgeElapsedWindowsSql;

    public bool IsDeadlock(DbException exception) => Classify && exception.Message.Contains(DeadlockMarker, StringComparison.Ordinal);

    private static bool TakeFailure(ref int remaining)
    {
        if (Volatile.Read(ref remaining) <= 0)
        {
            return false;
        }

        Interlocked.Decrement(ref remaining);
        return true;
    }
}
