using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
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
    /// <summary>How long a blocked caller is watched before concluding it really is blocked.</summary>
    private static readonly TimeSpan BlockedWindow = TimeSpan.FromSeconds(2);

    protected abstract MigrationEngine Engine { get; }

    protected abstract string ConnectionString { get; }

    /// <summary>The same server, pointed at <paramref name="database"/>.</summary>
    protected abstract string ConnectionStringFor(string database);

    protected abstract Task CreateDatabaseAsync(string database);

    [Fact]
    public async Task RunExclusive_ShouldBlockASecondCaller_UntilTheFirstReleases()
    {
        var firstHoldsLock = new TaskCompletionSource();
        var firstMayRelease = new TaskCompletionSource();
        var secondAcquiredLock = new TaskCompletionSource();

        var first = Task.Run(() => MigrationLock.RunExclusive(Engine, ConnectionString, () =>
        {
            firstHoldsLock.SetResult();
            firstMayRelease.Task.Wait();
        }));

        await firstHoldsLock.Task;

        var second = Task.Run(() => MigrationLock.RunExclusive(
            Engine, ConnectionString, secondAcquiredLock.SetResult));

        // The second caller must still be waiting: this is the race the lock exists to prevent, so it is
        // asserted directly rather than inferred from the absence of a migration collision.
        var winner = await Task.WhenAny(secondAcquiredLock.Task, Task.Delay(BlockedWindow));
        Assert.NotSame(secondAcquiredLock.Task, winner);

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

        var first = Task.Run(() => MigrationLock.RunExclusive(Engine, ConnectionString, () =>
        {
            firstHoldsLock.SetResult();
            firstMayRelease.Task.Wait();
        }));

        await firstHoldsLock.Task;

        var otherDatabaseRan = false;
        var second = Task.Run(() => MigrationLock.RunExclusive(
            Engine, ConnectionStringFor(OtherDatabase), () => otherDatabaseRan = true));

        // Must complete while the first caller is still holding the lock on the other database.
        var winner = await Task.WhenAny(second, Task.Delay(BlockedWindow));
        firstMayRelease.SetResult();
        await first;
        await second;

        Assert.Same(second, winner);
        Assert.True(otherDatabaseRan);
    }
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
