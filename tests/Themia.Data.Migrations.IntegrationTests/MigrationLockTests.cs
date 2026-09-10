using System.Diagnostics;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Testcontainers.MariaDb;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Themia.Data.Migrations.IntegrationTests;

/// <summary>
/// Proves the boot lock on every engine Themia ships a migration processor for. The contract is the same
/// in all three: while one caller holds the lock nobody else runs, and callers targeting *different*
/// databases never contend.
/// </summary>
public abstract class MigrationLockTestsBase
{
    /// <summary>
    /// How long a caller that should SUCCEED is given. Deliberately generous: exceeding it is a genuine
    /// failure (the caller is wrongly contending), never merely a slow machine, so a large value costs nothing
    /// on a green run and removes the wall-clock sensitivity a tight bound would have.
    /// </summary>
    private static readonly TimeSpan CompletionWindow = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long a caller that should be BLOCKED is watched before concluding it really is blocked — measured
    /// against this engine's own uncontended round trip rather than a fixed constant.
    /// </summary>
    /// <remarks>
    /// A fixed window silently loses all power when the uncontended path is slower than the window: on MariaDB
    /// an unpooled connect costs seconds (a per-connection reverse-DNS lookup), so a 3-second window let this
    /// test pass with the lock acquisition removed entirely. Calibrating instead means the assertion stays
    /// meaningful on any engine and any CI hardware, and the timing run also warms the DNS cache.
    /// </remarks>
    private TimeSpan MeasureBlockedWindow() =>
        TimeSpan.FromMilliseconds(Math.Max(3_000, MeasureUncontendedRun().TotalMilliseconds * 5));

    /// <summary>
    /// One full uncontended <c>RunExclusive</c> — connect, acquire, release — so timing assertions can be
    /// expressed relative to this engine's own round trip on this machine rather than a fixed constant.
    /// </summary>
    private TimeSpan MeasureUncontendedRun()
    {
        var started = Stopwatch.StartNew();
        MigrationLock.RunExclusive(Engine, ConnectionString, Options, () => { });
        return started.Elapsed;
    }

    protected abstract MigrationEngine Engine { get; }

    protected abstract string ConnectionString { get; }

    /// <summary>The same server, pointed at <paramref name="database"/>.</summary>
    protected abstract string ConnectionStringFor(string database);

    protected abstract Task CreateDatabaseAsync(string database);

    /// <summary>
    /// Asserts what this engine attaches as the timeout's inner exception. This is the only per-engine part
    /// of <see cref="RunExclusive_ShouldReportATimeout_WhenTheLockIsHeldPastTheLockTimeout"/>, and on
    /// PostgreSQL it is the assertion with teeth — see that test's remarks.
    /// </summary>
    /// <param name="cause">The <c>MigrationLockException</c>'s inner exception.</param>
    protected abstract void AssertServerEnforcedTheWait(Exception? cause);

    [Fact]
    public async Task RunExclusive_ShouldBlockASecondCaller_UntilTheFirstReleases()
    {
        var blockedWindow = MeasureBlockedWindow();

        var firstHoldsLock = new TaskCompletionSource();
        var firstMayRelease = new TaskCompletionSource();
        var secondEnteredRunExclusive = new TaskCompletionSource();
        var secondAcquiredLock = new TaskCompletionSource();

        var first = RunOnDedicatedThread(() => MigrationLock.RunExclusive(Engine, ConnectionString, Options, () =>
        {
            firstHoldsLock.SetResult();
            firstMayRelease.Task.Wait();
        }));

        await firstHoldsLock.Task;

        var second = RunOnDedicatedThread(() =>
        {
            // Signalled from inside the worker, immediately before the call that must block. Without this the
            // test could not tell "blocked on the lock" from "never got scheduled", and would pass even if the
            // lock did nothing at all.
            secondEnteredRunExclusive.SetResult();
            MigrationLock.RunExclusive(Engine, ConnectionString, Options, secondAcquiredLock.SetResult);
        });

        await secondEnteredRunExclusive.Task;

        // The second caller is running and has entered RunExclusive, so failing to acquire within the window
        // means it is genuinely blocked on the lock — the race this class exists to prevent.
        var blocked = await Task.WhenAny(secondAcquiredLock.Task, Task.Delay(blockedWindow));
        Assert.NotSame(secondAcquiredLock.Task, blocked);

        firstMayRelease.SetResult();
        await first;
        await second;

        Assert.True(secondAcquiredLock.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RunExclusive_ShouldNotContend_AcrossDifferentDatabases()
    {
        // PostgreSQL advisory locks and MySQL's GET_LOCK are keyed server-wide, not per database, so an
        // unscoped key would make two unrelated Themia applications sharing one server queue behind each
        // other's migrations for no reason.
        const string OtherDatabase = "themia_lock_probe";
        await CreateDatabaseAsync(OtherDatabase);

        var firstHoldsLock = new TaskCompletionSource();
        var firstMayRelease = new TaskCompletionSource();

        var first = RunOnDedicatedThread(() => MigrationLock.RunExclusive(Engine, ConnectionString, Options, () =>
        {
            firstHoldsLock.SetResult();
            firstMayRelease.Task.Wait();
        }));

        await firstHoldsLock.Task;

        var otherDatabaseRan = false;
        var second = RunOnDedicatedThread(() => MigrationLock.RunExclusive(
            Engine, ConnectionStringFor(OtherDatabase), Options, () => otherDatabaseRan = true));

        // Must complete while the first caller still holds the lock on the other database. If the scopes
        // wrongly collided this cannot finish at all until firstMayRelease is set below, so the assertion
        // fails deterministically rather than depending on how fast the machine is.
        var winner = await Task.WhenAny(second, Task.Delay(CompletionWindow));
        var completedWhileFirstHeldTheLock = ReferenceEquals(second, winner);

        firstMayRelease.SetResult();
        await first;
        await second;

        Assert.True(completedWhileFirstHeldTheLock, "the second caller contended with a different database's lock");
        Assert.True(otherDatabaseRan);
    }

    /// <summary>
    /// A caller that waits out its <see cref="ThemiaMigrationOptions.LockTimeout"/> must give up with the
    /// TIMEOUT-specific error, in roughly the timeout, with the engine's own timeout evidence preserved.
    /// </summary>
    /// <remarks>
    /// Nothing else covers lock EXPIRY: the other tests here use a five-minute timeout precisely so it never
    /// fires. That gap let the PostgreSQL adapter lose the <c>SET statement_timeout = …;</c> prefix in front
    /// of <c>pg_advisory_lock</c> without a single test noticing. Losing it removes the server-side wait
    /// bound, so no 57014 is ever raised, <c>TryAcquireLock</c> never returns false, and the wait instead
    /// degrades to Npgsql's <c>CommandTimeout</c> (LockTimeout + 30s) — a different exception type that
    /// arrives through the generic "failed to acquire the migration lock" wrap rather than the "timed out
    /// after {timeout}" one operators are taught to look for.
    /// <para>
    /// Three assertions are what make that regression class visible again: the message must be the
    /// timeout-specific one and not the generic wrap; the elapsed time must sit far below the driver's
    /// fallback; and <see cref="AssertServerEnforcedTheWait"/> must find the engine's own timeout evidence
    /// (on PostgreSQL, a <c>PostgresException</c> whose SQLSTATE is 57014 — the only positive proof that
    /// <c>statement_timeout</c>, not the driver, ended the wait).
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunExclusive_ShouldReportATimeout_WhenTheLockIsHeldPastTheLockTimeout()
    {
        // Calibrated, and taken before the lock is held so it measures the uncontended path.
        var uncontended = MeasureUncontendedRun();

        var firstHoldsLock = new TaskCompletionSource();
        var firstMayRelease = new TaskCompletionSource();

        var first = RunOnDedicatedThread(() => MigrationLock.RunExclusive(Engine, ConnectionString, Options, () =>
        {
            firstHoldsLock.SetResult();
            firstMayRelease.Task.Wait();
        }));

        await firstHoldsLock.Task;

        var migrated = false;
        MigrationLockException? failure = null;
        var started = Stopwatch.StartNew();
        var second = RunOnDedicatedThread(() => failure = Assert.Throws<MigrationLockException>(
            () => MigrationLock.RunExclusive(
                Engine, ConnectionString, ExpiringOptions, () => migrated = true)));

        // No Task.WhenAny guard needed: the waiter is bounded on both sides — by the server at
        // ExpiryTimeout and, if that bound is missing, by the driver at ExpiryTimeout + 30s — so it cannot
        // hang here, and letting it run to completion is what makes the elapsed measurement meaningful.
        await second;
        var elapsed = started.Elapsed;

        firstMayRelease.SetResult();
        await first;

        Assert.NotNull(failure);
        Assert.False(migrated, "the migration body ran even though the lock was never granted");

        Assert.Contains("timed out after", failure!.Message, StringComparison.Ordinal);
        Assert.Contains(ExpiryTimeout.ToString(), failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("failed to acquire", failure.Message, StringComparison.Ordinal);

        // Well under the driver fallback (ExpiryTimeout + MigrationLock's 30s command-timeout grace), and
        // scaled off this engine's own round trip so a slow container start cannot make it flaky.
        var driverFallbackFloor = ExpiryTimeout + TimeSpan.FromSeconds(30);
        var bound = ExpiryTimeout + TimeSpan.FromMilliseconds(
            Math.Min(15_000, Math.Max(5_000, uncontended.TotalMilliseconds * 3)));
        Assert.True(bound < driverFallbackFloor, $"the bound ({bound}) must stay below the driver fallback ({driverFallbackFloor})");
        Assert.InRange(elapsed, TimeSpan.FromSeconds(1), bound);

        AssertServerEnforcedTheWait(failure.InnerException);
    }

    /// <summary>
    /// Lock waits block a whole thread, and these tests deliberately park one for the duration. Dedicated
    /// threads keep them off the thread pool, so pool starvation cannot masquerade as "blocked on the lock".
    /// </summary>
    private static Task RunOnDedicatedThread(Action work)
    {
        var completion = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                work();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }) { IsBackground = true };
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// A lock timeout far larger than any window these tests use. The timeout is not what they exercise, and a
    /// tight one silently breaks them: the calibrated blocked-window can approach it, so the waiter times out
    /// and throws instead of staying blocked as the test intends.
    /// </summary>
    private static ThemiaMigrationOptions Options => new() { LockTimeout = TimeSpan.FromMinutes(5) };

    /// <summary>
    /// The one place a SHORT timeout is wanted: long enough that a granted lock is never mistaken for a
    /// timeout, short enough that the whole expiry costs seconds per engine.
    /// </summary>
    private static readonly TimeSpan ExpiryTimeout = TimeSpan.FromSeconds(2);

    private static ThemiaMigrationOptions ExpiringOptions => new() { LockTimeout = ExpiryTimeout };
}

[Trait("Category", "Integration")]
public class MigrationLockPostgresTests : MigrationLockTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    protected override MigrationEngine Engine => MigrationEngine.Postgres;

    protected override string ConnectionString => container.GetConnectionString();

    protected override string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// PostgreSQL is the engine where this assertion has teeth. <c>pg_advisory_lock</c> takes no timeout
    /// argument and <c>lock_timeout</c> does not apply to advisory locks, so the ONLY server-side wait bound
    /// is the <c>SET statement_timeout = …;</c> the adapter prefixes onto the acquire statement — and
    /// SQLSTATE 57014 (<c>query_canceled</c>) is the only positive evidence it was applied. A driver-level
    /// command timeout surfaces as a different exception type entirely, so asserting the type AND the
    /// SQLSTATE is what tells the two apart.
    /// </summary>
    protected override void AssertServerEnforcedTheWait(Exception? cause)
    {
        var postgres = Assert.IsType<PostgresException>(cause);
        Assert.Equal("57014", postgres.SqlState);
    }

    protected override async Task CreateDatabaseAsync(string database)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // CREATE DATABASE takes no parameters; the name is a test-local constant, not external input.
        command.CommandText = $"CREATE DATABASE {database}";
        await command.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[Trait("Category", "Integration")]
public class MigrationLockMySqlTests : MigrationLockTestsBase, IAsyncLifetime
{
    // Runs as root: the default container user cannot CREATE DATABASE, which the cross-database test needs.
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").WithUsername("root").Build();

    protected override MigrationEngine Engine => MigrationEngine.MySql;

    protected override string ConnectionString => container.GetConnectionString();

    protected override string ConnectionStringFor(string database) =>
        new MySqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// <c>GET_LOCK(name, timeout)</c> carries its own wait bound and reports a lapsed wait as the value 0,
    /// not as an error, so there is no engine exception to attach. Null is therefore the correct — and
    /// asserted — outcome: a non-null cause here would mean the wait ended by some other route (the driver
    /// severing the command), which is the failure this test exists to catch.
    /// </summary>
    protected override void AssertServerEnforcedTheWait(Exception? cause) => Assert.Null(cause);

    protected override async Task CreateDatabaseAsync(string database)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE IF NOT EXISTS {database}";
        await command.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();
}

/// <summary>
/// MigrationEngine.MySql covers MariaDB too, and the two diverge exactly where this lock lives: a NEGATIVE
/// GET_LOCK timeout means "wait forever" on MySQL 8 but is not portable. This leg proves the positive timeout
/// the lock actually sends works on both.
/// </summary>
[Trait("Category", "Integration")]
public class MigrationLockMariaDbTests : MigrationLockTestsBase, IAsyncLifetime
{
    // MariaDbBuilder, not MySqlBuilder: the latter's readiness probe runs mysqladmin, which MariaDB 11 does
    // not ship (it is mariadb-admin), so the container never reports ready and the test hangs.
    private readonly MariaDbContainer container = new MariaDbBuilder("mariadb:11").WithUsername("root").Build();

    protected override MigrationEngine Engine => MigrationEngine.MySql;

    protected override string ConnectionString => container.GetConnectionString();

    protected override string ConnectionStringFor(string database) =>
        new MySqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// <c>GET_LOCK(name, timeout)</c> carries its own wait bound and reports a lapsed wait as the value 0,
    /// not as an error, so there is no engine exception to attach. Null is therefore the correct — and
    /// asserted — outcome: a non-null cause here would mean the wait ended by some other route (the driver
    /// severing the command), which is the failure this test exists to catch.
    /// </summary>
    protected override void AssertServerEnforcedTheWait(Exception? cause) => Assert.Null(cause);

    protected override async Task CreateDatabaseAsync(string database)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE IF NOT EXISTS {database}";
        await command.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[Trait("Category", "Integration")]
public class MigrationLockSqlServerTests : MigrationLockTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    protected override MigrationEngine Engine => MigrationEngine.SqlServer;

    protected override string ConnectionString => container.GetConnectionString();

    protected override string ConnectionStringFor(string database) =>
        new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database }.ConnectionString;

    /// <summary>
    /// <c>sp_getapplock</c> carries its own <c>@LockTimeout</c> and reports a lapsed wait as return code -1,
    /// not as an error, so there is no engine exception to attach. Null is therefore the correct — and
    /// asserted — outcome: a non-null cause here would mean the wait ended by some other route (the driver
    /// severing the command), which is the failure this test exists to catch.
    /// </summary>
    protected override void AssertServerEnforcedTheWait(Exception? cause) => Assert.Null(cause);

    protected override async Task CreateDatabaseAsync(string database)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID('{database}') IS NULL CREATE DATABASE [{database}]";
        await command.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();
}
