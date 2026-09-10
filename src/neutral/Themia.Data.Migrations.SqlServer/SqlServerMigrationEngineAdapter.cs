using System.Data;
using System.Data.Common;
using FluentMigrator.Runner;
using Microsoft.Data.SqlClient;

namespace Themia.Data.Migrations.SqlServer;

/// <summary>SQL Server implementation of <see cref="IMigrationEngineAdapter"/>.</summary>
public sealed class SqlServerMigrationEngineAdapter : IMigrationEngineAdapter
{
    /// <inheritdoc />
    public MigrationEngine Engine => MigrationEngine.SqlServer;

    /// <inheritdoc />
    public string DisplayName => "SQL Server";

    /// <inheritdoc />
    public void ConfigureRunner(IMigrationRunnerBuilder builder) => builder.AddSqlServer();

    /// <inheritdoc />
    /// <remarks>
    /// Pooling is switched off for the lock connection on purpose. It is held for the entire migration, so
    /// a pooled slot would be occupied the whole time — which is what breaks a deployment configured with
    /// a maximum pool size of one, where the runner could then never get a connection of its own. It also
    /// avoids depending on the pool's reset-on-return to drop a session lock.
    /// </remarks>
    public DbConnection CreateUnpooledConnection(string connectionString) =>
        new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

    /// <inheritdoc />
    public bool TryAcquireLock(DbConnection connection, string scope, TimeSpan timeout, out Exception? timeoutCause)
    {
        // sp_getapplock reports a lapsed wait as return code -1, never as an exception, so there is no
        // engine exception to hand back.
        timeoutCause = null;
        // sp_getapplock is already database-scoped, so its resource name needs no database qualifier.
        // 'Session' ownership outlives the per-migration transactions the runner opens. Return codes:
        // 0/1 granted, -1 timeout, -2 cancelled, -3 deadlock victim, -999 parameter error.
        using var command = CreateApplockCommand(connection, "sp_getapplock", scope, timeout, out var result);
        MigrationLock.AddParameter(command, "@LockMode", "Exclusive");
        MigrationLock.AddParameter(command, "@LockTimeout", (int)timeout.TotalMilliseconds);
        command.ExecuteNonQuery();

        // Fail CLOSED. The return code is the only proof the lock was granted, so anything that is not an
        // explicit non-negative int — DBNull, an unset parameter — must be treated as "not granted".
        // Reading it the other way round would let MigrateUp run unprotected.
        if (result.Value is not int code)
        {
            throw new MigrationLockException(
                "Themia.Data.Migrations: sp_getapplock returned no status, so the migration lock cannot be " +
                "confirmed as granted.");
        }

        if (code == -1)
        {
            return false;
        }

        if (code < 0)
        {
            throw new MigrationLockException(
                $"Themia.Data.Migrations: sp_getapplock did not grant the migration lock (returned {code}).");
        }

        return true;
    }

    /// <inheritdoc />
    public void ReleaseLock(DbConnection connection, string scope)
    {
        using var command = CreateApplockCommand(connection, "sp_releaseapplock", scope, timeout: null, out var result);
        command.ExecuteNonQuery();
        if (result.Value is not 0)
        {
            throw new MigrationLockException(
                $"Themia.Data.Migrations: sp_releaseapplock did not report the migration lock as released for " +
                $"scope '{scope}' (returned {result.Value ?? "NULL"}).");
        }
    }

    private static DbCommand CreateApplockCommand(
        DbConnection connection, string procedure, string scope, TimeSpan? timeout, out DbParameter returnValue)
    {
        var command = timeout is null
            ? connection.CreateCommand()
            : MigrationLock.CreateWaitingCommand(connection, procedure, timeout.Value);
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;
        MigrationLock.AddParameter(command, "@Resource", MigrationLock.TextKey(scope));
        MigrationLock.AddParameter(command, "@LockOwner", "Session");

        returnValue = command.CreateParameter();
        returnValue.ParameterName = "@Result";
        returnValue.DbType = DbType.Int32;
        returnValue.Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add(returnValue);
        return command;
    }
}
