using System.Reflection;
using FluentMigrator.Runner;
using FluentMigrator.Runner.VersionTableInfo;
using FluentMigrator.Runner.Exceptions;
using FluentMigrator.Runner.Initialization;
using Microsoft.Extensions.DependencyInjection;

namespace Themia.Data.Migrations;

/// <summary>
/// Neutral entry point that applies FluentMigrator migrations through the processor for a
/// chosen <see cref="MigrationEngine"/>. Shared by every Themia neutral core and framework module
/// so the per-engine runner wiring lives in exactly one place (DECISION #6: FluentMigrator is the
/// single schema authority).
/// </summary>
public static class ThemiaMigrations
{
    /// <summary>
    /// Applies all pending FluentMigrator migrations found in <paramref name="migrationAssemblies"/>
    /// against <paramref name="connectionString"/> using the <paramref name="engine"/>'s processor.
    /// Runs synchronously (<c>MigrateUp</c>).
    /// </summary>
    /// <remarks>
    /// Safe to call from every instance of a horizontally-scaled application. The run is serialized behind
    /// the engine's session-level advisory lock, scoped to the target database, so instances booting
    /// simultaneously apply pending migrations one at a time instead of racing on the same DDL. An instance
    /// that finds the lock held waits for it, up to
    /// <see cref="ThemiaMigrationOptions.DefaultLockTimeout"/>. Supply a
    /// <see cref="ThemiaMigrationOptions.Logger"/> via the other overload to make a contended wait visible in
    /// the boot log — an orchestrator's startup probe will usually kill a waiting instance long before the
    /// timeout expires, and without a logger that leaves no trace.
    /// </remarks>
    /// <param name="engine">The target database engine.</param>
    /// <param name="connectionString">Connection string for the migration runner. Required.</param>
    /// <param name="migrationAssemblies">
    /// One or more assemblies scanned for <c>[Migration]</c> types. At least one is required, and the
    /// supplied set must contain at least one migration — passing assemblies with no <c>[Migration]</c>
    /// types is rejected rather than silently applying nothing.
    /// </param>
    /// <exception cref="ArgumentException">The connection string is null/whitespace, no assemblies were supplied, or the assemblies contain no <c>[Migration]</c> types.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="migrationAssemblies"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="engine"/> is not a known engine.</exception>
    /// <exception cref="InvalidOperationException">A <c>Themia.*</c> migration assembly was built from a different major.minor than this runner (see <see cref="MigrationAssemblyVersion"/>), or the migrations could not be loaded (e.g. duplicate version numbers) or failed to apply; the message names the engine.</exception>
    public static void Run(MigrationEngine engine, string connectionString, params Assembly[] migrationAssemblies) =>
        Run(engine, connectionString, options: null, migrationAssemblies);

    /// <inheritdoc cref="Run(MigrationEngine, string, Assembly[])"/>
    /// <param name="engine">The target database engine.</param>
    /// <param name="connectionString">Connection string for the migration runner. Required.</param>
    /// <param name="options">
    /// Migration-lock settings (wait timeout and a logger for lock diagnostics). Pass <see langword="null"/>
    /// for the defaults.
    /// </param>
    /// <param name="migrationAssemblies">
    /// One or more assemblies scanned for <c>[Migration]</c> types. At least one is required, and the
    /// supplied set must contain at least one migration — passing assemblies with no <c>[Migration]</c>
    /// types is rejected rather than silently applying nothing.
    /// </param>
    /// <remarks>
    /// Deliberately not <c>params</c>: with it, a three-argument call whose last argument is <c>null</c> would
    /// be ambiguous between this overload and the <c>params</c> one. Pass a collection expression —
    /// <c>Run(engine, cs, options, [typeof(X).Assembly])</c>.
    /// </remarks>
    public static void Run(
        MigrationEngine engine,
        string connectionString,
        ThemiaMigrationOptions? options,
        Assembly[] migrationAssemblies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(migrationAssemblies);
        if (migrationAssemblies.Length == 0)
            throw new ArgumentException("At least one migration assembly is required.", nameof(migrationAssemblies));

        // One source of truth for per-engine knowledge (processor + display name). Resolved up front so an
        // unknown engine fails as a clean guard before any infrastructure is built.
        var (addProcessor, displayName) = Describe(engine);

        // Every assembly is version-checked BEFORE any of them is applied: a mixed set must fail without
        // having half-migrated the database, and this costs two reflection reads with no connection open.
        foreach (var migrationAssembly in migrationAssemblies)
        {
            MigrationAssemblyVersion.Verify(migrationAssembly);
        }

        // ONE RUNNER PER ASSEMBLY, each with its own version ledger. A single runner over every assembly
        // would put them all back in one table, which is the whole defect: two migrations carrying the
        // same number then make one of them a silent no-op, and Themia's own modules already collided
        // that way (see ThemiaVersionTable). Every caller passes exactly one assembly today; the loop is
        // what keeps the multi-assembly overload from quietly reintroducing the shared ledger.
        foreach (var migrationAssembly in migrationAssemblies)
        {
            RunAssembly(engine, connectionString, options, addProcessor, displayName, migrationAssembly);
        }
    }

    private static void RunAssembly(
        MigrationEngine engine,
        string connectionString,
        ThemiaMigrationOptions? options,
        Action<IMigrationRunnerBuilder> addProcessor,
        string displayName,
        Assembly migrationAssembly)
    {
        var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                addProcessor(rb);
                rb.WithGlobalConnectionString(connectionString)
                  .ScanIn(migrationAssembly).For.Migrations();
            })
            // Registered outside ConfigureRunner deliberately: .For.Migrations() scans for [Migration]
            // types only, so an IVersionTableMetaData sitting in the scanned assembly would never be
            // picked up. It has to be handed to the container explicitly.
            .AddSingleton<IVersionTableMetaData>(new ThemiaVersionTable(migrationAssembly))
            .BuildServiceProvider(false);

        var scope = provider.CreateScope();
        var bodyFaulted = true;
        try
        {
            RunCore(scope.ServiceProvider, engine, connectionString, options, displayName, migrationAssembly);
            bodyFaulted = false;
        }
        finally
        {
            // NOT `using`. When a using-variable's Dispose throws while an exception is already in
            // flight, the Dispose exception REPLACES it — and FluentMigrator's processor disposes by
            // calling RollbackTransaction(), which throws InvalidOperationException("This SqlTransaction
            // has completed") whenever the transaction was already killed. A migration that lost a
            // deadlock or timed out therefore reported the rollback failure and lost the SqlException
            // that caused it: the operator saw a zombied-transaction message instead of "deadlock" or
            // "permission denied", and the carefully worded wraps below were discarded in exactly the
            // case they exist for. It also breaks every caller that retries on SQL error numbers, since
            // what reaches their catch is no longer a SqlException.
            //
            // A dispose failure is a consequence, never a cause. It is reported only when there is
            // nothing better to report — i.e. when the body completed and the disposal is the only thing
            // that went wrong.
            DisposeQuietly(scope, provider, bodyFaulted);
        }
    }

    /// <summary>
    /// Disposes the migration scope and provider without letting a dispose-time failure replace the
    /// exception the body already reported.
    /// </summary>
    /// <param name="scope">The migration scope.</param>
    /// <param name="provider">The provider that owns it.</param>
    /// <param name="bodyFaulted">
    /// Whether the body threw. The runtime offers no way to ask "is an exception unwinding through this
    /// finally", so the caller records it — and rethrowing from a finally while one is would reintroduce
    /// exactly the masking this method exists to prevent.
    /// </param>
    internal static void DisposeQuietly(IServiceScope scope, IDisposable provider, bool bodyFaulted)
    {
        Exception? disposeFailure = null;

        try
        {
            scope.Dispose();
        }
        catch (Exception ex)
        {
            disposeFailure = ex;
        }

        try
        {
            provider.Dispose();
        }
        catch (Exception ex)
        {
            // First failure wins: the scope disposes first and owns the processor, so its error is the
            // one closer to the cause.
            disposeFailure ??= ex;
        }

        if (disposeFailure is null || bodyFaulted)
        {
            return;
        }

        // Reported only when nothing else was: migrations applied, and the runner could not tear down.
        // Worth surfacing rather than swallowing — a processor that cannot dispose may be holding a
        // connection or a transaction open.
        throw new InvalidOperationException(
            "Themia.Data.Migrations: the migration runner failed to dispose cleanly. The migrations "
            + "themselves completed; see the inner exception.", disposeFailure);
    }

    private static void RunCore(
        IServiceProvider serviceProvider,
        MigrationEngine engine,
        string connectionString,
        ThemiaMigrationOptions? options,
        string displayName,
        Assembly migrationAssembly)
    {

        // Fail fast if the supplied assemblies carry no migrations: discovery happens in memory (no DB
        // connection), so a wrong/empty assembly is caught before MigrateUp would silently no-op and leave
        // the schema uncreated. Discovery is independent of applied state, so idempotent re-runs still pass.
        // (MigrateUp re-enumerates internally; this extra in-memory pass is startup-once and negligible.)
        int migrationCount;
        try
        {
            migrationCount = serviceProvider.GetRequiredService<IMigrationInformationLoader>().LoadMigrations().Count;
        }
        catch (MissingMigrationsException)
        {
            migrationCount = 0;
        }
        catch (Exception ex)
        {
            // Duplicate version numbers and other discovery failures are real migration errors — surface
            // them through a wrap (not raw), with a message that fits the load stage rather than DDL/permissions.
            throw new InvalidOperationException(
                $"Themia.Data.Migrations: failed to load migrations for {displayName}. " +
                "The supplied migration assemblies could not be enumerated (e.g. duplicate migration version numbers).", ex);
        }

        if (migrationCount == 0)
            throw new ArgumentException(
                $"Assembly '{migrationAssembly.GetName().Name}' contains no FluentMigrator [Migration] types; "
                + "nothing would be applied.",
                nameof(migrationAssembly));

        var runner = serviceProvider.GetRequiredService<IMigrationRunner>();

        try
        {
            // Serialized across instances: N of them booting at once would otherwise all see the same
            // migration pending and apply it concurrently (see MigrationLock).
            MigrationLock.RunExclusive(engine, connectionString, options ?? new ThemiaMigrationOptions(), runner.MigrateUp);
        }
        catch (MigrationLockException ex)
        {
            // Kept separate from the DDL wrap below: a lock failure never reached a migration, so pointing the
            // operator at DDL permissions would send them auditing grants for an outage that has another cause.
            throw new InvalidOperationException(
                $"Themia.Data.Migrations: could not take the migration lock for {displayName}, so no " +
                "migrations were applied. See the inner exception for the lock failure.", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Themia.Data.Migrations: failed to apply migrations against {displayName}. " +
                "Verify the connection string and that the principal has DDL permissions.", ex);
        }
    }

    private static (Action<IMigrationRunnerBuilder> AddProcessor, string DisplayName) Describe(MigrationEngine engine) => engine switch
    {
        MigrationEngine.Postgres => (rb => rb.AddPostgres(), "PostgreSQL"),
        MigrationEngine.MySql => (rb => rb.AddMySql8(), "MySQL"),
        MigrationEngine.SqlServer => (rb => rb.AddSqlServer(), "SQL Server"),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine."),
    };
}
