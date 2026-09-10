using System.Data.Common;
using FluentMigrator.Runner;

namespace Themia.Data.Migrations;

/// <summary>
/// One database engine's migration behaviour: which FluentMigrator processor to use, how to open the boot
/// lock's dedicated connection, and that engine's advisory-lock SQL. Implemented by
/// <c>Themia.Data.Migrations.PostgreSql</c>, <c>Themia.Data.Migrations.MySql</c> and
/// <c>Themia.Data.Migrations.SqlServer</c>; each package registers its adapter with
/// <see cref="MigrationEngineRegistry"/> through an <c>AddThemiaDataMigrations{Engine}()</c> extension
/// method.
/// </summary>
public interface IMigrationEngineAdapter
{
    /// <summary>The engine this adapter implements.</summary>
    MigrationEngine Engine { get; }

    /// <summary>The engine's human-readable name, used in diagnostic messages (e.g. <c>"PostgreSQL"</c>).</summary>
    string DisplayName { get; }

    /// <summary>Adds this engine's FluentMigrator processor to <paramref name="builder"/> (e.g. <c>rb.AddPostgres()</c>).</summary>
    /// <param name="builder">The runner builder being configured.</param>
    void ConfigureRunner(IMigrationRunnerBuilder builder);

    /// <summary>
    /// Opens a dedicated, unpooled connection for the boot lock. The connection is held for the entire
    /// migration run, so pooling is disabled — a pooled slot occupied for that long would starve the
    /// runner's own connections on a server configured with a small pool.
    /// </summary>
    /// <param name="connectionString">The connection string to open.</param>
    /// <returns>An unopened, unpooled connection.</returns>
    DbConnection CreateUnpooledConnection(string connectionString);

    /// <summary>
    /// Opens an ordinary <b>pooled</b> connection for this engine — the general-purpose factory a package
    /// that needs a <see cref="DbConnection"/> for the engine it was handed should route through, instead
    /// of naming a driver type of its own.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="CreateUnpooledConnection"/> rather than a reuse of it. That
    /// one disables pooling because the migration lock is held for the whole migration; a caller doing
    /// ordinary short-lived work — a Quartz ADO job store, an execution-history writer — must not inherit
    /// that, or every one of its connections would pay a fresh handshake and never return to the pool.
    /// <para>
    /// The reason this exists at all: a package that constructed <c>new NpgsqlConnection(…)</c> /
    /// <c>new SqlConnection(…)</c> behind a <c>switch</c> on <see cref="MigrationEngine"/> had to declare
    /// every driver it might switch to, which put <c>Microsoft.Data.SqlClient</c> (and with it
    /// <c>Microsoft.IdentityModel.*</c> and <c>Azure.Identity</c>) into PostgreSQL-only adopters' images
    /// — coord #0126. Routing through the adapter means only the one engine package the adopter chose
    /// carries a driver.
    /// </para>
    /// </remarks>
    /// <param name="connectionString">The connection string to open.</param>
    /// <returns>An unopened, pooled connection.</returns>
    DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Attempts to acquire this engine's session-level advisory lock, scoped to <paramref name="scope"/>,
    /// waiting up to <paramref name="timeout"/>.
    /// </summary>
    /// <param name="connection">The open, unpooled connection returned by <see cref="CreateUnpooledConnection"/>.</param>
    /// <param name="scope">The lock's identity — see <see cref="MigrationLock"/>.</param>
    /// <param name="timeout">How long to wait for the lock before giving up.</param>
    /// <param name="timeoutCause">
    /// When the wait timed out, the engine exception that reported it — PostgreSQL cancels the waiting
    /// statement and raises <c>57014</c>, so the <c>PostgresException</c> carrying that SQLSTATE lands here.
    /// <see langword="null"/> when the engine reports a timeout as an ordinary return value instead
    /// (MySQL's <c>GET_LOCK</c> returning 0, SQL Server's <c>sp_getapplock</c> returning -1). Attached as the
    /// inner exception of the resulting <c>MigrationLockException</c>, which is what lets an operator tell a
    /// server-enforced lock timeout apart from the driver severing the command.
    /// </param>
    /// <returns><see langword="true"/> if the lock was granted; <see langword="false"/> if the wait timed out.</returns>
    bool TryAcquireLock(DbConnection connection, string scope, TimeSpan timeout, out Exception? timeoutCause);

    /// <summary>
    /// Releases the advisory lock previously acquired for <paramref name="scope"/> on
    /// <paramref name="connection"/>.
    /// </summary>
    /// <param name="connection">The connection the lock was acquired on.</param>
    /// <param name="scope">The lock's identity — see <see cref="MigrationLock"/>.</param>
    void ReleaseLock(DbConnection connection, string scope);
}
