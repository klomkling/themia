using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace Themia.Data.Migrations;

/// <summary>
/// Serializes a migration run across simultaneously-booting application instances using the target
/// engine's session-level advisory lock.
/// </summary>
/// <remarks>
/// FluentMigrator skips migrations already recorded in <c>VersionInfo</c>, so migrate-on-boot is a no-op
/// once everything is applied. The unsafe window is several instances booting at once: all read
/// <c>VersionInfo</c>, all see the same migration pending, and all apply it concurrently — check-then-apply
/// is not atomic across connections, so they collide on DDL and insert duplicate version rows. Holding one
/// advisory lock over <c>MigrateUp</c> makes them run one at a time: the first applies the pending
/// migrations, the rest wait, then acquire the lock, see everything applied, and skip. No separate migration
/// job is needed and the "every instance migrates, already-applied is skipped" model survives horizontal scale.
///
/// The lock is taken on a dedicated connection. The migrations themselves run on the runner's own
/// connections and are unaffected — only another *instance* contends.
/// </remarks>
internal static class MigrationLock
{
    /// <summary>
    /// Namespaces the lock so it cannot collide with an advisory lock the application takes for its own
    /// reasons on the same server.
    /// </summary>
    private const string KeyNamespace = "themia:data:migrations:";

    /// <summary>Prefix for the string-named locks (MySQL, SQL Server). Kept short — MySQL caps names at 64 chars.</summary>
    private const string TextKeyPrefix = "themia_migrate_";

    /// <summary>
    /// Opens a dedicated connection, acquires the migration lock, runs <paramref name="migrate"/>, then
    /// releases. Waits indefinitely for the lock: a booting instance genuinely cannot proceed until the
    /// instance ahead of it has finished migrating.
    /// </summary>
    internal static void RunExclusive(MigrationEngine engine, string connectionString, Action migrate)
    {
        using var connection = CreateConnection(engine, connectionString);
        connection.Open();

        // The *live* database name, which avoids per-engine connection-string parsing and reflects whatever
        // database the server actually put us in.
        var scope = KeyNamespace + connection.Database;

        Acquire(engine, connection, scope);
        try
        {
            migrate();
        }
        finally
        {
            Release(engine, connection, scope);
        }
    }

    private static void Acquire(MigrationEngine engine, DbConnection connection, string scope)
    {
        switch (engine)
        {
            case MigrationEngine.Postgres:
                // Advisory locks are keyed by a bare bigint and are CLUSTER-global rather than database-scoped,
                // so the database name is folded into the key: two Themia apps sharing one PostgreSQL cluster
                // must not serialize against each other.
                using (var command = CreateWaitingCommand(connection, "SELECT pg_advisory_lock(@key)"))
                {
                    AddParameter(command, "key", NumericKey(scope));
                    command.ExecuteNonQuery();
                }

                break;

            case MigrationEngine.MySql:
                // GET_LOCK is likewise server-global, and its name is capped at 64 characters, so the scope is
                // hashed rather than embedded verbatim. A negative timeout waits forever; the result is 1 on
                // success, 0 on timeout, NULL on error.
                using (var command = CreateWaitingCommand(connection, "SELECT GET_LOCK(@name, -1)"))
                {
                    AddParameter(command, "name", TextKey(scope));
                    var acquired = command.ExecuteScalar();
                    if (acquired is not 1L and not 1)
                        throw new InvalidOperationException(
                            $"GET_LOCK('{TextKey(scope)}') did not grant the migration lock (returned '{acquired ?? "NULL"}').");
                }

                break;

            case MigrationEngine.SqlServer:
                // sp_getapplock is already database-scoped, so its resource name needs no database qualifier.
                // 'Session' ownership outlives the per-migration transactions the runner opens. A non-negative
                // return code means granted.
                using (var command = CreateApplockCommand(connection, "sp_getapplock", scope, out var result))
                {
                    AddParameter(command, "@LockMode", "Exclusive");
                    AddParameter(command, "@LockTimeout", -1);
                    command.ExecuteNonQuery();
                    if (result.Value is int code && code < 0)
                        throw new InvalidOperationException(
                            $"sp_getapplock did not grant the migration lock (returned {code}).");
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine.");
        }
    }

    private static void Release(MigrationEngine engine, DbConnection connection, string scope)
    {
        switch (engine)
        {
            case MigrationEngine.Postgres:
                using (var command = CreateWaitingCommand(connection, "SELECT pg_advisory_unlock(@key)"))
                {
                    AddParameter(command, "key", NumericKey(scope));
                    command.ExecuteNonQuery();
                }

                break;

            case MigrationEngine.MySql:
                using (var command = CreateWaitingCommand(connection, "SELECT RELEASE_LOCK(@name)"))
                {
                    AddParameter(command, "name", TextKey(scope));
                    command.ExecuteNonQuery();
                }

                break;

            case MigrationEngine.SqlServer:
                using (var command = CreateApplockCommand(connection, "sp_releaseapplock", scope, out _))
                {
                    command.ExecuteNonQuery();
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine.");
        }
    }

    private static DbConnection CreateConnection(MigrationEngine engine, string connectionString) => engine switch
    {
        MigrationEngine.Postgres => new NpgsqlConnection(connectionString),
        MigrationEngine.MySql => new MySqlConnection(connectionString),
        MigrationEngine.SqlServer => new SqlConnection(connectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine."),
    };

    /// <summary>
    /// A command with no client-side timeout. The default 30s would abort the wait long before a slow
    /// migration on the instance ahead has finished — the whole point is to wait it out.
    /// </summary>
    private static DbCommand CreateWaitingCommand(DbConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        return command;
    }

    private static DbCommand CreateApplockCommand(
        DbConnection connection, string procedure, string scope, out DbParameter returnValue)
    {
        var command = connection.CreateCommand();
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = 0;
        AddParameter(command, "@Resource", TextKey(scope));
        AddParameter(command, "@LockOwner", "Session");

        returnValue = command.CreateParameter();
        returnValue.ParameterName = "@Result";
        returnValue.DbType = DbType.Int32;
        returnValue.Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add(returnValue);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// Derives the 64-bit key for <c>pg_advisory_lock</c> from <paramref name="scope"/>.
    /// </summary>
    /// <remarks>
    /// Uses SHA-256 rather than <see cref="string.GetHashCode()"/> deliberately: string hash codes are
    /// randomized per process on .NET, so every instance would compute a *different* key and none of them
    /// would contend — the lock would silently do nothing. This value is a wire format between processes and
    /// must never change.
    /// </remarks>
    internal static long NumericKey(string scope) =>
        BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));

    /// <summary>
    /// Derives the string lock name for MySQL's <c>GET_LOCK</c> and SQL Server's <c>sp_getapplock</c>.
    /// 31 characters, well inside MySQL's 64-character cap. Stable across processes for the same reason as
    /// <see cref="NumericKey"/>.
    /// </summary>
    internal static string TextKey(string scope) =>
        TextKeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)).AsSpan(0, 8)).ToLowerInvariant();
}
