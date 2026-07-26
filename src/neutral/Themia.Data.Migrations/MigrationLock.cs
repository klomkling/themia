using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
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
/// migrations, the rest wait, then acquire the lock, see everything applied, and skip.
///
/// The lock is taken on a dedicated, <b>unpooled</b> connection. The migrations themselves run on the
/// runner's own connections and are unaffected — only another *instance* contends.
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
    /// Extra client-side headroom over the lock timeout, so the *server* enforces the wait and reports a
    /// precise timeout rather than the driver severing the command first.
    /// </summary>
    private static readonly TimeSpan CommandTimeoutGrace = TimeSpan.FromSeconds(30);

    /// <summary>PostgreSQL's error code for a statement cancelled by <c>statement_timeout</c>.</summary>
    private const string PostgresQueryCanceled = "57014";

    /// <summary>What the release attempt established about the lock we thought we were holding.</summary>
    private enum LockRelease
    {
        /// <summary>We held it and it is now released.</summary>
        Released,

        /// <summary>The server says we did not hold it — the session was reaped while migrating.</summary>
        NotHeld,

        /// <summary>The release could not be carried out (typically a dead connection).</summary>
        Failed,
    }

    /// <summary>
    /// Opens a dedicated connection, acquires the migration lock, runs <paramref name="migrate"/>, then
    /// releases.
    /// </summary>
    /// <exception cref="MigrationLockException">The lock could not be opened, acquired, or was not granted before the timeout.</exception>
    internal static void RunExclusive(
        MigrationEngine engine, string connectionString, ThemiaMigrationOptions options, Action migrate)
    {
        using var connection = CreateConnection(engine, connectionString);

        try
        {
            connection.Open();
        }
        catch (Exception ex)
        {
            // Distinct from a migration failure: nothing has touched the schema yet. Note that Run now needs
            // TWO concurrent connections (this one, held for the duration, plus the runner's) where one used
            // to suffice — hence the explicit hint, since a server-side connection cap surfaces here.
            throw new MigrationLockException(
                "Themia.Data.Migrations: could not open the connection used to take the migration lock. " +
                "Applying migrations needs two concurrent connections — one holds the lock while the runner " +
                "uses its own — so verify the server's connection limit is at least two for this principal.", ex);
        }

        var scope = LockScope(connection, options.Logger);

        Acquire(engine, connection, scope, options);
        try
        {
            migrate();
        }
        catch
        {
            // The migration failure is the operator's real signal. Release best-effort and let the original
            // exception propagate untouched — a throwing release here would replace it and erase the only
            // diagnostic naming the migration that actually failed.
            TryRelease(engine, connection, scope);
            throw;
        }

        // Success path: now the release result is meaningful. "You did not hold this lock" means the session
        // was reaped mid-migration (idle reaper, PgBouncer, wait_timeout), which means the mutual exclusion
        // this class exists to provide was not actually in force and another instance may have migrated
        // concurrently. Warn rather than throw: the migration itself committed, and crashing a
        // successfully-migrated instance would trade a reported anomaly for a crash-loop.
        var outcome = TryRelease(engine, connection, scope);
        if (outcome != LockRelease.Released)
        {
            options.Logger?.LogWarning(
                "Themia.Data.Migrations: the migration lock for scope {LockScope} was no longer held when " +
                "releasing it ({Outcome}). The lock session was most likely dropped while migrating, so " +
                "another instance could have migrated concurrently — check for duplicate VersionInfo rows.",
                scope, outcome);
        }
    }

    /// <summary>
    /// The lock's identity: the database this connection is bound to, lower-cased.
    /// </summary>
    /// <remarks>
    /// Lower-casing matters. <c>connection.Database</c> echoes the connection string (Npgsql returns
    /// <c>Settings.Database ?? Settings.Username</c>) rather than anything the server normalised, so
    /// <c>Database=App</c> and <c>Database=app</c> — the same database on a case-insensitive engine — would
    /// otherwise hash to two unrelated keys and never contend. The failure mode of folding case is
    /// over-serialisation (two genuinely distinct, differently-cased databases sharing one lock), which is
    /// merely slower; the failure mode of not folding is two instances migrating the same schema at once.
    /// </remarks>
    private static string LockScope(DbConnection connection, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(connection.Database))
        {
            // Nothing to scope by, so every Themia app on this server shares one lock. Safe (it only
            // over-serialises) but worth saying out loud.
            logger?.LogWarning(
                "Themia.Data.Migrations: the connection reports no database name, so the migration lock " +
                "cannot be scoped to one database and will be shared server-wide.");
        }

        return NormalizeScope(connection.Database);
    }

    /// <summary>Pure scope derivation, split out so the normalisation rules are directly testable.</summary>
    internal static string NormalizeScope(string? database) =>
        KeyNamespace + (database?.Trim() ?? string.Empty).ToLowerInvariant();

    private static void Acquire(
        MigrationEngine engine, DbConnection connection, string scope, ThemiaMigrationOptions options)
    {
        var timeout = options.LockTimeout > TimeSpan.Zero ? options.LockTimeout : ThemiaMigrationOptions.DefaultLockTimeout;

        options.Logger?.LogInformation(
            "Themia.Data.Migrations: acquiring the migration lock for scope {LockScope} (waiting up to {LockTimeout}). " +
            "Another instance migrating the same database will hold this lock until it finishes.",
            scope, timeout);

        try
        {
            AcquireCore(engine, connection, scope, timeout);
        }
        catch (MigrationLockException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new MigrationLockException(
                $"Themia.Data.Migrations: failed to acquire the migration lock for scope '{scope}'.", ex);
        }
    }

    private static void AcquireCore(MigrationEngine engine, DbConnection connection, string scope, TimeSpan timeout)
    {
        switch (engine)
        {
            case MigrationEngine.Postgres:
                // Advisory locks are keyed by a bare bigint and are CLUSTER-global rather than database-scoped,
                // so the database name is folded into the key: two Themia apps sharing one PostgreSQL cluster
                // must not serialize against each other.
                //
                // pg_advisory_lock takes no timeout argument, and lock_timeout does not apply to advisory
                // locks — statement_timeout does, and it reports a precise 57014 rather than relying on the
                // driver to sever the command.
                using (var command = CreateWaitingCommand(
                    connection,
                    $"SET statement_timeout = {(int)timeout.TotalMilliseconds}; SELECT pg_advisory_lock(@key)",
                    timeout))
                {
                    AddParameter(command, "key", NumericKey(scope));
                    try
                    {
                        command.ExecuteNonQuery();
                    }
                    catch (PostgresException ex) when (ex.SqlState == PostgresQueryCanceled)
                    {
                        throw TimedOut(scope, timeout, ex);
                    }
                }

                break;

            case MigrationEngine.MySql:
                // GET_LOCK is likewise server-global, and its name is capped at 64 characters, so the scope is
                // hashed rather than embedded verbatim. The timeout is a positive number of seconds: a
                // NEGATIVE timeout means "wait forever" on MySQL 8 but is not portable to MariaDB, which this
                // engine also covers. Result is 1 granted, 0 timed out, NULL on error.
                using (var command = CreateWaitingCommand(connection, "SELECT GET_LOCK(@name, @timeout)", timeout))
                {
                    AddParameter(command, "name", TextKey(scope));
                    AddParameter(command, "timeout", Math.Max(1, (int)timeout.TotalSeconds));
                    var granted = command.ExecuteScalar();
                    if (granted is 0L or 0)
                        throw TimedOut(scope, timeout, null);
                    if (granted is not 1L and not 1)
                        throw new MigrationLockException(
                            $"Themia.Data.Migrations: GET_LOCK('{TextKey(scope)}') failed to grant the " +
                            $"migration lock (returned '{granted ?? "NULL"}').");
                }

                break;

            case MigrationEngine.SqlServer:
                // sp_getapplock is already database-scoped, so its resource name needs no database qualifier.
                // 'Session' ownership outlives the per-migration transactions the runner opens. Return codes:
                // 0/1 granted, -1 timeout, -2 cancelled, -3 deadlock victim, -999 parameter error.
                using (var command = CreateApplockCommand(connection, "sp_getapplock", scope, timeout, out var result))
                {
                    AddParameter(command, "@LockMode", "Exclusive");
                    AddParameter(command, "@LockTimeout", (int)timeout.TotalMilliseconds);
                    command.ExecuteNonQuery();

                    // Fail CLOSED. The return code is the only proof the lock was granted, so anything that is
                    // not an explicit non-negative int — DBNull, an unset parameter — must be treated as "not
                    // granted". Reading it the other way round would let MigrateUp run unprotected.
                    if (result.Value is not int code)
                        throw new MigrationLockException(
                            "Themia.Data.Migrations: sp_getapplock returned no status, so the migration lock " +
                            "cannot be confirmed as granted.");
                    if (code == -1)
                        throw TimedOut(scope, timeout, null);
                    if (code < 0)
                        throw new MigrationLockException(
                            $"Themia.Data.Migrations: sp_getapplock did not grant the migration lock (returned {code}).");
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine.");
        }
    }

    private static MigrationLockException TimedOut(string scope, TimeSpan timeout, Exception? inner) =>
        new($"Themia.Data.Migrations: timed out after {timeout} waiting for the migration lock for scope " +
            $"'{scope}'. Another instance is most likely still migrating, or is holding the lock without " +
            "making progress.", inner);

    /// <summary>
    /// Releases the lock without ever throwing, reporting what the server said about our ownership.
    /// </summary>
    private static LockRelease TryRelease(MigrationEngine engine, DbConnection connection, string scope)
    {
        try
        {
            return engine switch
            {
                // pg_advisory_unlock and RELEASE_LOCK both report whether the caller actually held the lock,
                // so the result is read rather than discarded — it is the only way to notice that a reaped
                // session voided the mutual exclusion.
                MigrationEngine.Postgres => ReadRelease(connection, "SELECT pg_advisory_unlock(@key)", ("key", NumericKey(scope))),
                MigrationEngine.MySql => ReadRelease(connection, "SELECT RELEASE_LOCK(@name)", ("name", TextKey(scope))),
                MigrationEngine.SqlServer => ReleaseApplock(connection, scope),
                _ => LockRelease.Failed,
            };
        }
        catch (Exception)
        {
            // A dead connection is the common case here, and it is itself evidence the session (and therefore
            // the lock) is gone. Never rethrow: on the failure path this runs while a migration exception is
            // in flight, and that exception must survive.
            return LockRelease.Failed;
        }
    }

    private static LockRelease ReadRelease(DbConnection connection, string sql, (string Name, object Value) parameter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, parameter.Name, parameter.Value);
        var held = command.ExecuteScalar();
        return held switch
        {
            true or 1L or 1 => LockRelease.Released,
            false or 0L or 0 => LockRelease.NotHeld,
            _ => LockRelease.Failed,
        };
    }

    private static LockRelease ReleaseApplock(DbConnection connection, string scope)
    {
        using var command = CreateApplockCommand(connection, "sp_releaseapplock", scope, timeout: null, out var result);
        command.ExecuteNonQuery();
        return result.Value switch
        {
            0 => LockRelease.Released,
            int => LockRelease.NotHeld,
            _ => LockRelease.Failed,
        };
    }

    private static DbConnection CreateConnection(MigrationEngine engine, string connectionString) => engine switch
    {
        // Pooling is switched off for the lock connection on purpose. It is held for the entire migration, so
        // a pooled slot would be occupied the whole time — which is what breaks a deployment configured with
        // a maximum pool size of one, where the runner could then never get a connection of its own. It also
        // avoids depending on the pool's reset-on-return to drop a session lock.
        MigrationEngine.Postgres => new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString),
        MigrationEngine.MySql => new MySqlConnection(
            new MySqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString),
        MigrationEngine.SqlServer => new SqlConnection(
            new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine."),
    };

    /// <summary>
    /// A command whose client-side timeout sits <em>above</em> the lock timeout, so the server's own wait
    /// bound is what expires first and the caller gets a precise "timed out waiting for the lock" rather than
    /// a generic driver timeout. The 30s ADO.NET default would otherwise abort every contended wait.
    /// </summary>
    private static DbCommand CreateWaitingCommand(DbConnection connection, string sql, TimeSpan timeout)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = (int)(timeout + CommandTimeoutGrace).TotalSeconds;
        return command;
    }

    private static DbCommand CreateApplockCommand(
        DbConnection connection, string procedure, string scope, TimeSpan? timeout, out DbParameter returnValue)
    {
        var command = timeout is null
            ? connection.CreateCommand()
            : CreateWaitingCommand(connection, procedure, timeout.Value);
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;
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
