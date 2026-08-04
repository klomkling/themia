using System.Data.Common;
using System.Threading;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// Wraps a real engine's <see cref="IChallengeDialect"/> to make the <see cref="IChallengeDialect.ConsumeSql"/>
/// race deterministic in a test, instead of relying on incidental thread/connection-open timing — the
/// same technique as <c>Themia.Challenges.Tests.RaceGatingChallengeDialect</c>, reused here over a real
/// engine dialect instead of SQLite.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, and why bare <c>Task.WhenAll</c> was not enough.</b> Confirmed directly against
/// SQL Server in this environment: two <see cref="IChallengeService.VerifyAsync"/> calls started via
/// <c>Task.WhenAll</c> do not reliably overlap at the database — one call's entire round trip (open
/// connection, <see cref="IChallengeDialect.SelectLiveByScopeSql"/>, hash compare, consume) can finish
/// before the other call's connection has even finished opening. When that happens the second call's
/// <see cref="IChallengeDialect.SelectLiveByScopeSql"/> correctly finds nothing live (the first call
/// already consumed the row) and falls into <c>ChallengeService.ClassifyMissingAsync</c> — which does
/// not distinguish an already-consumed row from one that never existed, so it reports
/// <see cref="ChallengeVerifyOutcome.NotFound"/> instead of <see cref="ChallengeVerifyOutcome.Consumed"/>.
/// That makes a bare-<c>Task.WhenAll</c> test flaky (pass only when the incidental timing happens to
/// overlap) rather than a real proof that <see cref="IChallengeDialect.ConsumeSql"/>'s guarded
/// <c>UPDATE</c> is atomic under genuine concurrent connections — the one thing this class exists to
/// force deterministically.
/// </para>
/// <para>
/// Unlike the SQLite test double (which subclasses <c>SqliteConnection</c>/<c>SqliteCommand</c>
/// directly), this gates at the <see cref="IChallengeDialect.ConsumeSql"/> <b>property getter</b> rather
/// than at the ADO.NET connection/command layer: <c>ChallengeService.VerifyAsync</c> reads
/// <c>dialect.ConsumeSql</c> exactly once, immediately before executing it, only after it has already
/// found the row live and hash-matched it — so gating the getter blocks a racer at exactly the same
/// point the SQLite decorator blocks the guarded <c>UPDATE</c> itself, without needing to subclass
/// provider-specific ADO.NET types (some of which, e.g. MySqlConnector's, are sealed).
/// </para>
/// </remarks>
internal sealed class RaceGatingChallengeDialect : IChallengeDialect
{
    private readonly IChallengeDialect inner;
    private readonly Barrier barrier;

    /// <summary>Wraps <paramref name="inner"/>, gating its <see cref="ConsumeSql"/> getter on
    /// <paramref name="barrier"/> — a two-party <see cref="Barrier"/> shared by both racing
    /// <see cref="ChallengeService"/> instances.</summary>
    public RaceGatingChallengeDialect(IChallengeDialect inner, Barrier barrier)
    {
        this.inner = inner;
        this.barrier = barrier;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => inner.CreateConnection();

    /// <inheritdoc />
    public string InsertSql => inner.InsertSql;

    /// <inheritdoc />
    public string SelectLiveByScopeSql => inner.SelectLiveByScopeSql;

    /// <inheritdoc />
    public string SelectLiveByTokenHashSql => inner.SelectLiveByTokenHashSql;

    /// <inheritdoc />
    public string SelectMostRecentByScopeSql => inner.SelectMostRecentByScopeSql;

    /// <inheritdoc />
    /// <remarks>Blocks until both racing calls have reached this point — i.e. both have already run
    /// <see cref="SelectLiveByScopeSql"/> and found the row live — before either is allowed to proceed to
    /// the guarded <c>UPDATE</c>. Whichever executes first commits (1 row affected); the second's
    /// <c>WHERE consumed_at IS NULL</c> guard then excludes the row the first just consumed (0 rows
    /// affected) — the real engine's row-level locking arbitrates the race, not this class.</remarks>
    public string ConsumeSql
    {
        get
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            return inner.ConsumeSql;
        }
    }

    /// <inheritdoc />
    public string RecordAttemptSql => inner.RecordAttemptSql;

    /// <inheritdoc />
    public string InvalidateLiveForScopeSql => inner.InvalidateLiveForScopeSql;

    /// <inheritdoc />
    public string PurgeExpiredSql => inner.PurgeExpiredSql;

    /// <inheritdoc />
    public string IncrementWindowSql => inner.IncrementWindowSql;

    /// <inheritdoc />
    public string DecrementWindowSql => inner.DecrementWindowSql;

    /// <inheritdoc />
    public string PurgeElapsedWindowsSql => inner.PurgeElapsedWindowsSql;
}
