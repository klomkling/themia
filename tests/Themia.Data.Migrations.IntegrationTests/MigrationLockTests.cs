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
    private TimeSpan MeasureBlockedWindow()
    {
        var started = Stopwatch.StartNew();
        MigrationLock.RunExclusive(Engine, ConnectionString, Options, () => { });
        var uncontended = started.Elapsed;

        return TimeSpan.FromMilliseconds(Math.Max(3_000, uncontended.TotalMilliseconds * 5));
    }

    protected abstract MigrationEngine Engine { get; }

    protected abstract string ConnectionString { get; }

    /// <summary>The same server, pointed at <paramref name="database"/>.</summary>
    protected abstract string ConnectionStringFor(string database);

    protected abstract Task CreateDatabaseAsync(string database);

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
}

[Trait("Category", "Integration")]
public class MigrationLockPostgresTests : MigrationLockTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    protected override MigrationEngine Engine => MigrationEngine.Postgres;

    protected override string ConnectionString => container.GetConnectionString();

    protected override string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString;

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
