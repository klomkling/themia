using System.Buffers.Binary;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

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
///
/// The engine-specific SQL lives in each <see cref="IMigrationEngineAdapter"/>
/// (<c>Themia.Data.Migrations.{PostgreSql,MySql,SqlServer}</c>); this class owns the orchestration
/// (open, acquire, run, release) plus the pure key-derivation helpers those adapters share.
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

    /// <summary>
    /// Opens a dedicated connection, acquires the migration lock, runs <paramref name="migrate"/>, then
    /// releases. Resolves <paramref name="engine"/> through <see cref="MigrationEngineRegistry"/>.
    /// </summary>
    /// <exception cref="MigrationLockException">The lock could not be opened, acquired, or was not granted before the timeout.</exception>
    internal static void RunExclusive(
        MigrationEngine engine, string connectionString, ThemiaMigrationOptions options, Action migrate) =>
        RunExclusive(MigrationEngineRegistry.Resolve(engine), connectionString, options, migrate);

    /// <inheritdoc cref="RunExclusive(MigrationEngine, string, ThemiaMigrationOptions, Action)"/>
    /// <param name="adapter">The engine adapter supplying the advisory-lock SQL and unpooled connection.</param>
    /// <param name="connectionString">Connection string for the lock connection.</param>
    /// <param name="options">Lock timeout and logger.</param>
    /// <param name="migrate">The migration body to run while the lock is held.</param>
    internal static void RunExclusive(
        IMigrationEngineAdapter adapter, string connectionString, ThemiaMigrationOptions options, Action migrate)
    {
        using var connection = adapter.CreateUnpooledConnection(connectionString);

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

        Acquire(adapter, connection, scope, options);
        try
        {
            migrate();
        }
        catch
        {
            // The migration failure is the operator's real signal. Release best-effort and let the original
            // exception propagate untouched — a throwing release here would replace it and erase the only
            // diagnostic naming the migration that actually failed.
            TryRelease(adapter, connection, scope, options.Logger);
            throw;
        }

        TryRelease(adapter, connection, scope, options.Logger);
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
        IMigrationEngineAdapter adapter, DbConnection connection, string scope, ThemiaMigrationOptions options)
    {
        var timeout = options.LockTimeout > TimeSpan.Zero ? options.LockTimeout : ThemiaMigrationOptions.DefaultLockTimeout;

        options.Logger?.LogInformation(
            "Themia.Data.Migrations: acquiring the migration lock for scope {LockScope} (waiting up to {LockTimeout}). " +
            "Another instance migrating the same database will hold this lock until it finishes.",
            scope, timeout);

        bool acquired;
        Exception? timeoutCause;
        try
        {
            acquired = adapter.TryAcquireLock(connection, scope, timeout, out timeoutCause);
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

        if (!acquired)
        {
            throw TimedOut(scope, timeout, timeoutCause);
        }
    }

    /// <summary>
    /// The timeout-specific failure, distinct from the generic "failed to acquire" wrap above. Operators are
    /// taught to look for this wording, and <paramref name="inner"/> is what proves the *server* enforced the
    /// wait (PostgreSQL's 57014) rather than the driver's command timeout severing it — the difference that
    /// tells a contended boot apart from a lock whose wait bound was never actually applied.
    /// </summary>
    private static MigrationLockException TimedOut(string scope, TimeSpan timeout, Exception? inner) =>
        new($"Themia.Data.Migrations: timed out after {timeout} waiting for the migration lock for scope " +
            $"'{scope}'. Another instance is most likely still migrating, or is holding the lock without " +
            "making progress.", inner);

    /// <summary>
    /// Releases the lock without ever letting a release failure escape — this runs on both the success path
    /// (where a failed release means the lock session was likely dropped while migrating, so another
    /// instance could have migrated concurrently) and the failure path (where a migration exception is
    /// already in flight and must survive). Either way a throwing adapter is treated as "release did not
    /// cleanly succeed" and logged, never propagated.
    /// </summary>
    private static void TryRelease(IMigrationEngineAdapter adapter, DbConnection connection, string scope, ILogger? logger)
    {
        try
        {
            adapter.ReleaseLock(connection, scope);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex,
                "Themia.Data.Migrations: failed to release the migration lock for scope {LockScope}. The lock " +
                "session was most likely dropped while migrating, so another instance could have migrated " +
                "concurrently — check for duplicate VersionInfo rows.",
                scope);
        }
    }

    /// <summary>
    /// A command whose client-side timeout sits <em>above</em> the lock timeout, so the server's own wait
    /// bound is what expires first and the caller gets a precise "timed out waiting for the lock" rather than
    /// a generic driver timeout. The 30s ADO.NET default would otherwise abort every contended wait.
    /// </summary>
    /// <remarks>Internal rather than private: shared by every engine adapter's lock SQL.</remarks>
    internal static DbCommand CreateWaitingCommand(DbConnection connection, string sql, TimeSpan timeout)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = (int)(timeout + CommandTimeoutGrace).TotalSeconds;
        return command;
    }

    /// <summary>Adds a named parameter to <paramref name="command"/>. Shared by every engine adapter's lock SQL.</summary>
    internal static void AddParameter(DbCommand command, string name, object value)
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
