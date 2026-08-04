using System.Data.Common;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace Themia.Challenges.Tests;

/// <summary>
/// Wraps <see cref="SqliteChallengeDialect"/> to make the <see cref="IChallengeDialect.ConsumeSql"/>
/// race deterministic in a test, instead of relying on incidental thread-pool timing.
/// </summary>
/// <remarks>
/// Two concurrent <c>VerifyAsync</c> calls racing to consume the same row is inherently timing
/// dependent — asserting on it via bare <c>Task.WhenAll</c> would make the single most important test
/// in the suite flaky. This forces the deterministic shape instead: every connection's execution of the
/// exact <see cref="IChallengeDialect.ConsumeSql"/> text blocks on a two-party <see cref="Barrier"/>
/// until <em>both</em> racing calls have reached it — i.e. both have already run their own
/// <see cref="IChallengeDialect.SelectLiveByScopeSql"/> and found the row live — before either is
/// allowed to execute the guarded <c>UPDATE</c>. SQLite's single-writer locking then serializes the two
/// releases: whichever executes first commits (1 row affected), and the second's <c>WHERE consumed_at
/// IS NULL</c> guard now excludes the row the first just consumed (0 rows affected) — exactly the
/// "someone else won the race" case <see cref="Internal.ChallengeService.VerifyAsync"/> must turn into
/// <see cref="ChallengeVerifyOutcome.Consumed"/> rather than <see cref="ChallengeVerifyOutcome.Verified"/>.
/// </remarks>
internal sealed class RaceGatingChallengeDialect : IChallengeDialect
{
    private readonly SqliteChallengeDialect inner;
    private readonly Barrier barrier;

    public RaceGatingChallengeDialect(string connectionString, Barrier barrier)
    {
        inner = new SqliteChallengeDialect(connectionString);
        this.barrier = barrier;
        ConnectionString = connectionString;
    }

    private string ConnectionString { get; }

    public DbConnection CreateConnection() => new GatingConnection(ConnectionString, ConsumeSql, barrier);

    public string InsertSql => inner.InsertSql;
    public string SelectLiveByScopeSql => inner.SelectLiveByScopeSql;
    public string SelectLiveByTokenHashSql => inner.SelectLiveByTokenHashSql;
    public string ConsumeSql => inner.ConsumeSql;
    public string RecordAttemptSql => inner.RecordAttemptSql;
    public string InvalidateLiveForScopeSql => inner.InvalidateLiveForScopeSql;
    public string PurgeExpiredSql => inner.PurgeExpiredSql;
    public string IncrementWindowSql => inner.IncrementWindowSql;
    public string SelectWindowCountsSql => inner.SelectWindowCountsSql;
    public string DecrementWindowSql => inner.DecrementWindowSql;
    public string PurgeElapsedWindowsSql => inner.PurgeElapsedWindowsSql;

    private sealed class GatingConnection : SqliteConnection
    {
        private readonly string gatedCommandText;
        private readonly Barrier barrier;

        public GatingConnection(string connectionString, string gatedCommandText, Barrier barrier)
            : base(connectionString)
        {
            this.gatedCommandText = gatedCommandText;
            this.barrier = barrier;
        }

        protected override DbCommand CreateDbCommand() => new GatingCommand(this, gatedCommandText, barrier);
    }

    private sealed class GatingCommand : SqliteCommand
    {
        private readonly string gatedCommandText;
        private readonly Barrier barrier;

        public GatingCommand(SqliteConnection connection, string gatedCommandText, Barrier barrier)
        {
            Connection = connection;
            this.gatedCommandText = gatedCommandText;
            this.barrier = barrier;
        }

        public override int ExecuteNonQuery()
        {
            if (CommandText == gatedCommandText)
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
            }

            return base.ExecuteNonQuery();
        }
    }
}
