using System.Data.Common;
using System.Threading;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// Wraps a real engine's <see cref="IChallengeDialect"/> and runs a hook when a statement is requested, i.e. at the
/// exact point between "the guess was judged" and "the statement runs". That is how the attempt-bound proofs force
/// the interleaving instead of hoping thread-pool timing produces it: a barrier before
/// <see cref="IChallengeDialect.RecordAttemptSql"/> holds every racer after its read, and a hook before
/// <see cref="IChallengeDialect.ConsumeSql"/> spends the budget behind a correct guess's back.
/// </summary>
internal sealed class StatementHookDialect : IChallengeDialect
{
    private readonly IChallengeDialect inner;

    /// <summary>Wraps <paramref name="inner"/>; either hook may be null.</summary>
    public StatementHookDialect(IChallengeDialect inner, Action? beforeConsume, Action? beforeRecordAttempt)
    {
        this.inner = inner;
        BeforeConsume = beforeConsume;
        BeforeRecordAttempt = beforeRecordAttempt;
    }

    /// <summary>Runs each time <see cref="ConsumeSql"/> is requested.</summary>
    public Action? BeforeConsume { get; set; }

    /// <summary>Runs each time <see cref="RecordAttemptSql"/> is requested.</summary>
    public Action? BeforeRecordAttempt { get; set; }

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
            BeforeConsume?.Invoke();
            return inner.ConsumeSql;
        }
    }

    /// <inheritdoc />
    public string RecordAttemptSql
    {
        get
        {
            BeforeRecordAttempt?.Invoke();
            return inner.RecordAttemptSql;
        }
    }

    /// <inheritdoc />
    public string InvalidateLiveForScopeSql(ChallengeTenancy tenancy)
    {
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

}
