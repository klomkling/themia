using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using MySqlConnector;

namespace Themia.Challenges.MySql;

/// <summary>
/// Wraps every command executed on a MySQL connection with a bounded retry on <c>ER_LOCK_DEADLOCK</c>
/// (MySQL error 1213) — see <see cref="MySqlChallengeDialect"/>'s remarks for why this lives in the
/// dialect rather than <c>ChallengeService</c>.
/// </summary>
/// <remarks>
/// Mirrors <c>Themia.Messaging.MySql.MySqlMessagingDialect.ClaimAsync</c>'s
/// <c>catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.LockDeadlock &amp;&amp; attempt &lt; MaxDeadlockRetries)</c>
/// guard, applied at the connection/command level instead of around one named operation, because
/// <see cref="IChallengeDialect"/> is a "supply SQL text" seam, not a "perform this operation" one —
/// <c>ChallengeService</c> is the single engine-agnostic caller that executes every statement via Dapper
/// against whatever <see cref="IChallengeDialect.CreateConnection"/> hands back, so it has no per-engine
/// hook to retry from. Wrapping the connection here keeps that contract intact: every statement this
/// dialect's connections execute is transparently retried on a transient deadlock, with no change to
/// <c>ChallengeService</c> or the shared <see cref="IChallengeDialect"/> interface.
/// <para>
/// <b>Composition, not inheritance.</b> Both <see cref="MySqlConnection"/> and <see cref="MySqlCommand"/>
/// are <see langword="sealed"/>, so this wraps an inner instance of each and forwards every ADO.NET
/// member rather than subclassing (the technique <c>Themia.Challenges.Tests.SqliteChallengeDialect</c>'s
/// race-gating helper uses, since <c>Microsoft.Data.Sqlite</c>'s equivalents are not sealed).
/// </para>
/// <para>
/// Both the synchronous and asynchronous execute paths are retried. <c>ChallengeService</c> always calls
/// the async path (Dapper's <c>ExecuteAsync</c>/<c>QueryAsync</c> against a real <see cref="DbCommand"/>
/// call <see cref="DbCommand.ExecuteNonQueryAsync(CancellationToken)"/> /
/// <c>ExecuteDbDataReaderAsync</c> directly, not the sync methods wrapped in a task), and MySqlConnector
/// implements true async I/O for those rather than falling back to <see cref="DbCommand"/>'s
/// sync-wrapped-in-a-task default — so only retrying the sync methods would silently never run.
/// </para>
/// <para>
/// <b>Retrying a whole command, not resuming mid-statement, is safe here.</b> A deadlock is per-statement:
/// InnoDB rolls back only the statement that lost the deadlock arbitration, and every statement this
/// dialect issues runs under autocommit (no explicit transaction spans more than one
/// <see cref="DbCommand"/>), so a retried command re-executes against whatever the database now holds,
/// never a partially-applied prior attempt. The one multi-statement command,
/// <see cref="MySqlChallengeDialect.IncrementWindowSql"/>, is safe to retry as a whole for the same
/// reason its own remarks give for using <c>ON DUPLICATE KEY UPDATE id = id</c>: if the seed
/// <c>INSERT</c> already committed before the following <c>UPDATE</c> deadlocked, re-running the
/// <c>INSERT</c> on retry is a no-op against the row that now exists, not a duplicate increment.
/// </para>
/// </remarks>
internal sealed class DeadlockRetryingConnection : DbConnection
{
    /// <summary>
    /// <c>Themia.Messaging.MySql.MySqlMessagingDialect.MaxDeadlockRetries</c> (3) is not reused here: that
    /// value was tuned for its own claim scenario, which is nowhere near as hot as
    /// <c>IncrementWindowSql</c>'s single-row rate-window bucket under many-way same-key contention — 3
    /// measurably still failed at <c>ConcurrencyTests.ConcurrencyLevel</c> (64) contention (confirmed
    /// directly: <c>ConcurrentIssues_ForTheSameKey_ShouldNotLoseACount</c> failed with an unretried
    /// <c>ER_LOCK_DEADLOCK</c> at 3). 30 matches the bound the test's own now-removed workaround used to
    /// survive the same benchmark reliably across repeated runs.
    /// </summary>
    internal const int MaxDeadlockRetries = 30;

    private readonly MySqlConnection inner;

    public DeadlockRetryingConnection(string connectionString) => inner = new MySqlConnection(connectionString);

    [AllowNull]
    public override string ConnectionString
    {
        get => inner.ConnectionString;
        set => inner.ConnectionString = value;
    }

    public override string Database => inner.Database;

    public override string DataSource => inner.DataSource;

    public override string ServerVersion => inner.ServerVersion;

    public override ConnectionState State => inner.State;

    public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

    public override void Close() => inner.Close();

    public override void Open() => inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand()
    {
        var command = new DeadlockRetryingCommand(inner.CreateCommand());
        command.Connection = this;
        return command;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();

    private sealed class DeadlockRetryingCommand : DbCommand
    {
        private readonly MySqlCommand inner;
        private DbConnection? connection;
        private DbTransaction? transaction;

        public DeadlockRetryingCommand(MySqlCommand inner) => this.inner = inner;

        [AllowNull]
        public override string CommandText
        {
            get => inner.CommandText;
            set => inner.CommandText = value;
        }

        public override int CommandTimeout
        {
            get => inner.CommandTimeout;
            set => inner.CommandTimeout = value;
        }

        public override CommandType CommandType
        {
            get => inner.CommandType;
            set => inner.CommandType = value;
        }

        public override UpdateRowSource UpdatedRowSource
        {
            get => inner.UpdatedRowSource;
            set => inner.UpdatedRowSource = value;
        }

        // The outer DeadlockRetryingConnection, stored for API correctness only — inner already carries
        // its own MySqlConnection reference from construction and is never re-pointed at this wrapper.
        protected override DbConnection? DbConnection
        {
            get => connection;
            set => connection = value;
        }

        protected override DbParameterCollection DbParameterCollection => inner.Parameters;

        protected override DbTransaction? DbTransaction
        {
            get => transaction;
            set
            {
                transaction = value;
                inner.Transaction = (MySqlTransaction?)value;
            }
        }

        public override bool DesignTimeVisible
        {
            get => inner.DesignTimeVisible;
            set => inner.DesignTimeVisible = value;
        }

        public override void Cancel() => inner.Cancel();

        public override void Prepare() => inner.Prepare();

        protected override DbParameter CreateDbParameter() => inner.CreateParameter();

        public override int ExecuteNonQuery() => Retry(inner.ExecuteNonQuery);

        public override object? ExecuteScalar() => Retry(inner.ExecuteScalar);

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Retry(() => inner.ExecuteReader(behavior));

        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) =>
            RetryAsync(() => inner.ExecuteNonQueryAsync(cancellationToken));

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken) =>
            RetryAsync(() => inner.ExecuteScalarAsync(cancellationToken));

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
            RetryAsync<DbDataReader>(async () => await inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false));

        // Only ER_LOCK_DEADLOCK (1213) is retried — see the type-level remarks on why this is safe to
        // retry as a whole command. Any other MySqlException (a genuine constraint violation, a syntax
        // error, a connection failure) is not transient and must surface immediately, not be silently
        // absorbed.
        private static T Retry<T>(Func<T> execute)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return execute();
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < MaxDeadlockRetries)
                {
                    // Transient InnoDB deadlock — the statement's own implicit transaction is already
                    // rolled back; retry it, after backing off (see BackoffDelay).
                    Thread.Sleep(BackoffDelay(attempt));
                }
            }
        }

        private static async Task<T> RetryAsync<T>(Func<Task<T>> execute)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await execute().ConfigureAwait(false);
                }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.LockDeadlock && attempt < MaxDeadlockRetries)
                {
                    // Transient InnoDB deadlock — the statement's own implicit transaction is already
                    // rolled back; retry it, after backing off (see BackoffDelay).
                    await Task.Delay(BackoffDelay(attempt)).ConfigureAwait(false);
                }
            }
        }

        // Full jitter, exponential, capped: at many-way contention on the same brand-new bucket, a fixed
        // or narrowly-jittered delay lets every retrying caller collide again on the very next attempt (a
        // thundering herd against one row) — confirmed directly, an earlier version of this method with
        // no delay at all still exhausted MaxDeadlockRetries under ConcurrencyTests.ConcurrencyLevel-way
        // contention. Randomizing across the *entire* [0, cap] range, not a small offset from a fixed
        // base, decorrelates retries across every concurrent caller instead of just spacing them out
        // slightly. Mirrors the backoff shape the test-side workaround this fix replaces used to prove
        // the same benchmark reliably.
        private static TimeSpan BackoffDelay(int attempt)
        {
            var capMs = Math.Min(500, 10 * (1 << Math.Min(attempt, 10)));
            return TimeSpan.FromMilliseconds(Random.Shared.Next(1, capMs));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
