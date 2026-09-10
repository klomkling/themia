using System.Data.Common;
using FluentMigrator.Runner;
using Npgsql;

namespace Themia.Data.Migrations.PostgreSql;

/// <summary>PostgreSQL implementation of <see cref="IMigrationEngineAdapter"/>.</summary>
public sealed class PostgresMigrationEngineAdapter : IMigrationEngineAdapter
{
    /// <summary>PostgreSQL's error code for a statement cancelled by <c>statement_timeout</c>.</summary>
    private const string QueryCanceled = "57014";

    /// <inheritdoc />
    public MigrationEngine Engine => MigrationEngine.Postgres;

    /// <inheritdoc />
    public string DisplayName => "PostgreSQL";

    /// <inheritdoc />
    public void ConfigureRunner(IMigrationRunnerBuilder builder) => builder.AddPostgres();

    /// <inheritdoc />
    /// <remarks>
    /// Pooling is switched off for the lock connection on purpose. It is held for the entire migration, so
    /// a pooled slot would be occupied the whole time — which is what breaks a deployment configured with
    /// a maximum pool size of one, where the runner could then never get a connection of its own. It also
    /// avoids depending on the pool's reset-on-return to drop a session lock.
    /// </remarks>
    public DbConnection CreateUnpooledConnection(string connectionString) =>
        new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public bool TryAcquireLock(DbConnection connection, string scope, TimeSpan timeout, out Exception? timeoutCause)
    {
        timeoutCause = null;
        // Advisory locks are keyed by a bare bigint and are CLUSTER-global rather than database-scoped, so
        // the database name is folded into the key: two Themia apps sharing one PostgreSQL cluster must
        // not serialize against each other.
        //
        // pg_advisory_lock takes no timeout argument, and lock_timeout does not apply to advisory locks —
        // statement_timeout does, and it reports a precise 57014 rather than relying on the driver to sever
        // the command.
        using var command = MigrationLock.CreateWaitingCommand(
            connection,
            $"SET statement_timeout = {(int)timeout.TotalMilliseconds}; SELECT pg_advisory_lock(@key)",
            timeout);
        MigrationLock.AddParameter(command, "key", MigrationLock.NumericKey(scope));
        try
        {
            command.ExecuteNonQuery();
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == QueryCanceled)
        {
            // Handed back rather than swallowed: 57014 is the only positive evidence that the SET
            // statement_timeout above is what ended the wait. Without it a caller cannot tell this apart
            // from Npgsql's own CommandTimeout severing the command, which is exactly the regression the
            // prefix's loss caused once already.
            timeoutCause = ex;
            return false;
        }
    }

    /// <inheritdoc />
    public void ReleaseLock(DbConnection connection, string scope)
    {
        // pg_advisory_unlock reports whether the caller actually held the lock, so the result is read
        // rather than discarded — it is the only way to notice that a reaped session voided the mutual
        // exclusion this lock exists to provide.
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_advisory_unlock(@key)";
        MigrationLock.AddParameter(command, "key", MigrationLock.NumericKey(scope));
        var held = command.ExecuteScalar();
        if (held is not (true or 1L or 1))
        {
            throw new MigrationLockException(
                $"Themia.Data.Migrations: pg_advisory_unlock did not report the migration lock as held for " +
                $"scope '{scope}' (returned '{held ?? "NULL"}').");
        }
    }
}
