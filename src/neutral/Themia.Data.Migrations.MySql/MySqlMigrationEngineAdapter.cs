using System.Data.Common;
using FluentMigrator.Runner;
using MySqlConnector;

namespace Themia.Data.Migrations.MySql;

/// <summary>
/// MySQL 8.0.13+ implementation of <see cref="IMigrationEngineAdapter"/>. The advisory-lock half also works
/// against MariaDB (<see cref="MigrationEngine.MySql"/>'s remarks) — that does not make MariaDB a supported
/// engine for Themia schemas in general.
/// </summary>
public sealed class MySqlMigrationEngineAdapter : IMigrationEngineAdapter
{
    /// <inheritdoc />
    public MigrationEngine Engine => MigrationEngine.MySql;

    /// <inheritdoc />
    public string DisplayName => "MySQL";

    /// <inheritdoc />
    public void ConfigureRunner(IMigrationRunnerBuilder builder) => builder.AddMySql8();

    /// <inheritdoc />
    /// <remarks>
    /// Pooling is switched off for the lock connection on purpose. It is held for the entire migration, so
    /// a pooled slot would be occupied the whole time — which is what breaks a deployment configured with
    /// a maximum pool size of one, where the runner could then never get a connection of its own. It also
    /// avoids depending on the pool's reset-on-return to drop a session lock.
    /// </remarks>
    public DbConnection CreateUnpooledConnection(string connectionString) =>
        new MySqlConnection(new MySqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public bool TryAcquireLock(DbConnection connection, string scope, TimeSpan timeout, out Exception? timeoutCause)
    {
        // GET_LOCK reports a lapsed wait as an ordinary 0, never as an exception, so there is no engine
        // exception to hand back.
        timeoutCause = null;
        // GET_LOCK is likewise server-global, and its name is capped at 64 characters, so the scope is
        // hashed rather than embedded verbatim. The timeout is a positive number of seconds: a NEGATIVE
        // timeout means "wait forever" on MySQL 8 but is not portable to MariaDB, which this engine also
        // covers. Result is 1 granted, 0 timed out, NULL on error.
        using var command = MigrationLock.CreateWaitingCommand(connection, "SELECT GET_LOCK(@name, @timeout)", timeout);
        MigrationLock.AddParameter(command, "name", MigrationLock.TextKey(scope));
        MigrationLock.AddParameter(command, "timeout", Math.Max(1, (int)timeout.TotalSeconds));
        var granted = command.ExecuteScalar();
        if (granted is 0L or 0)
        {
            return false;
        }

        if (granted is not 1L and not 1)
        {
            throw new MigrationLockException(
                $"Themia.Data.Migrations: GET_LOCK('{MigrationLock.TextKey(scope)}') failed to grant the " +
                $"migration lock (returned '{granted ?? "NULL"}').");
        }

        return true;
    }

    /// <inheritdoc />
    public void ReleaseLock(DbConnection connection, string scope)
    {
        // RELEASE_LOCK reports whether the caller actually held the lock, so the result is read rather than
        // discarded — it is the only way to notice that a reaped session voided the mutual exclusion this
        // lock exists to provide.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT RELEASE_LOCK(@name)";
        MigrationLock.AddParameter(command, "name", MigrationLock.TextKey(scope));
        var held = command.ExecuteScalar();
        if (held is not (true or 1L or 1))
        {
            throw new MigrationLockException(
                $"Themia.Data.Migrations: RELEASE_LOCK('{MigrationLock.TextKey(scope)}') did not report the " +
                $"migration lock as held for scope '{scope}' (returned '{held ?? "NULL"}').");
        }
    }
}
