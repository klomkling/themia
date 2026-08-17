using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Data.Migrations.IntegrationTests;

/// <summary>
/// Whether "is this migration pending?" is decided before or after the migration lock is taken.
/// </summary>
/// <remarks>
/// coord #0085 asked this, and it is the only mechanism anyone has proposed that turns a correct
/// adopt-what-exists migration into a boot crash: two instances both read an empty ledger, serialize on
/// the lock, and the second applies DDL the first has already committed. That produces
/// <c>42P07 relation … already exists</c> and a ledger row that was never written.
/// <para>
/// <see cref="ThemiaMigrationsPostgresTests.Run_AppliesEachMigrationOnce_WhenInstancesBootSimultaneously"/>
/// covers the same ground with six racing instances, but it can only fail when the interleaving happens to
/// land — a green run there proves nothing. Here the window is held open deliberately: the ledger is
/// completed while the run is parked on the lock, so a decision made before acquiring it is guaranteed to
/// be stale.
/// </para>
/// <para>
/// <see cref="ProbeMigration"/> is the right subject precisely because it is NOT guarded — it creates its
/// table unconditionally. Every shipping Themia migration checks for what it creates, which would mask a
/// stale read behind the guard; this one reports it.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PendingCheckInsideLockTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").WithCleanUp(true).Build();

    private string ConnectionString => container.GetConnectionString();

    [Fact]
    public async Task A_run_parked_on_the_lock_sees_the_ledger_the_holder_completed()
    {
        var assembly = typeof(ProbeMigration).Assembly;
        var ledger = new ThemiaVersionTable(assembly).TableName;

        // MigrationLock's key, recomputed here rather than reached into: it is a wire format between
        // processes, so a test that derived it any other way would stop contending the moment it drifted.
        var database = new NpgsqlConnectionStringBuilder(ConnectionString).Database!;
        var key = BinaryPrimitives.ReadInt64LittleEndian(
            SHA256.HashData(Encoding.UTF8.GetBytes("themia:data:migrations:" + database.ToLowerInvariant())));

        await using var holder = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString);
        await holder.OpenAsync();
        await ExecuteAsync(holder, $"SELECT pg_advisory_lock({key})");

        // The instance under test boots into a contended lock and parks.
        var run = Task.Run(() => ThemiaMigrations.Run(MigrationEngine.Postgres, ConnectionString, assembly));
        await WaitUntilParkedOnTheLockAsync();

        // The holder finishes its migration while the other instance is still waiting: the table exists and
        // the ledger says so. Written by hand because any path through ThemiaMigrations would queue behind
        // the very lock this test is holding.
        await ExecuteAsync(holder, "CREATE TABLE migrations_probe (\"Id\" integer NOT NULL PRIMARY KEY)");
        await ExecuteAsync(holder,
            $"CREATE TABLE \"{ledger}\" (\"Version\" bigint NOT NULL, \"AppliedOn\" timestamp NULL, "
            + "\"Description\" varchar(1024) NULL)");
        await ExecuteAsync(holder,
            $"INSERT INTO \"{ledger}\" (\"Version\", \"AppliedOn\", \"Description\") "
            + "VALUES (202606120001, now(), 'Themia.Data.Migrations probe table')");

        await ExecuteAsync(holder, $"SELECT pg_advisory_unlock({key})");

        // A pending set read before the lock still says 202606120001 is unapplied, so the run recreates
        // migrations_probe and fails 42P07 — wrapped by RunCore as InvalidOperationException.
        var failure = await Record.ExceptionAsync(() => run);
        Assert.Null(failure);

        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM \"{ledger}\""));
    }

    private async Task WaitUntilParkedOnTheLockAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await CountAsync("SELECT COUNT(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted") == 0)
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
