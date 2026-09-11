using System.Collections.Concurrent;

namespace Themia.Data.Migrations;

/// <summary>
/// Process-wide registry mapping each <see cref="MigrationEngine"/> to the <see cref="IMigrationEngineAdapter"/>
/// that implements it. An engine package (e.g. <c>Themia.Data.Migrations.PostgreSql</c>) populates this
/// through its <c>AddThemiaDataMigrations{Engine}()</c> extension method — an explicit call, never a
/// <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/>, because an assembly that is
/// merely referenced (and never otherwise used) is not guaranteed to load, so its module initializer might
/// never run.
/// </summary>
/// <remarks>
/// <see cref="ThemiaMigrations.Run(MigrationEngine, string, System.Reflection.Assembly[])"/> resolves
/// through this registry. Callers who already hold an <see cref="IMigrationEngineAdapter"/> — the four
/// families with an engine-specific entry point (Audit, Exceptional, Challenges, DataProtection) — can
/// skip it entirely via the <c>Run(IMigrationEngineAdapter, …)</c> overload.
/// </remarks>
public static class MigrationEngineRegistry
{
    private static readonly ConcurrentDictionary<MigrationEngine, IMigrationEngineAdapter> Adapters = new();

    /// <summary>
    /// Registers <paramref name="adapter"/> for its <see cref="IMigrationEngineAdapter.Engine"/>, replacing
    /// any adapter already registered for that engine. Idempotent and thread-safe — safe to call more than
    /// once (e.g. from multiple modules that each depend on the same engine package).
    /// </summary>
    /// <remarks>
    /// <b>Every engine-specific entry point calls this too</b> — <c>AddThemiaExceptionalPostgres</c>,
    /// <c>AddThemiaAudit{Engine}</c>, <c>AddThemiaChallenges{Engine}</c>,
    /// <c>PersistKeysToThemia{Engine}</c> — even though each already holds its adapter at compile time and
    /// needs no lookup of its own. The reason is the adopter, not the package: one who calls only
    /// engine-specific methods still has code resolving the SAME engine through this registry BY ENUM (any
    /// <c>Themia.Modules.*</c> constructor, <c>SchedulingSchema.Migrate</c>), and that resolution threw
    /// "no adapter is registered" at boot even though the engine package was plainly referenced — a crash
    /// loop on a version bump (coord #0126). The <c>THEMIA2001</c> build guard cannot catch it: it checks
    /// for the engine PACKAGE, which was present; MSBuild cannot see whether any C# calls
    /// <c>AddThemiaDataMigrations{Engine}()</c>. Registering on use closes that gap by construction.
    /// <para>
    /// This does not make <c>AddThemiaDataMigrations{Engine}()</c> optional. An adopter using
    /// <c>Themia.Scheduling</c> or a module with NO engine-specific package at all has nothing to trigger
    /// the side effect, and for them the explicit call remains required — and <c>THEMIA2001</c> genuinely
    /// fires, because in that case the package really is missing.
    /// </para>
    /// </remarks>
    /// <param name="adapter">The adapter to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="adapter"/> is <see langword="null"/>.</exception>
    public static void Add(IMigrationEngineAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        Adapters[adapter.Engine] = adapter;
    }

    /// <summary>
    /// Removes every registered adapter. Test-only, and deliberately <see langword="internal"/>: it mutates
    /// process-wide state, so exposing it publicly would put a hatch in the shipped surface that an
    /// application could use to unregister engines mid-run. Production startup has no reason to call it —
    /// registration accumulates as packages are added and is never undone. Reachable from the test
    /// assemblies named by <c>InternalsVisibleTo</c> in the csproj, which pin every class that calls it into
    /// a single non-parallel xUnit collection so no other class can resolve through the registry while it is
    /// cleared.
    /// </summary>
    internal static void Reset() => Adapters.Clear();

    /// <summary>
    /// Resolves the adapter registered for <paramref name="engine"/>.
    /// </summary>
    /// <param name="engine">The engine to resolve.</param>
    /// <returns>The registered adapter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="engine"/> is not a known engine.</exception>
    /// <exception cref="InvalidOperationException">No adapter is registered for <paramref name="engine"/>.</exception>
    public static IMigrationEngineAdapter Resolve(MigrationEngine engine)
    {
        var (packageName, methodName) = PackageFor(engine);

        if (Adapters.TryGetValue(engine, out var adapter))
        {
            return adapter;
        }

        throw new InvalidOperationException(
            $"MigrationEngine.{engine} was requested but no adapter is registered. Add a reference to " +
            $"{packageName} and call {methodName}().");
    }

    /// <summary>Validates <paramref name="engine"/> and names the package/method that registers it.</summary>
    private static (string PackageName, string MethodName) PackageFor(MigrationEngine engine) => engine switch
    {
        MigrationEngine.Postgres => ("Themia.Data.Migrations.PostgreSql", "AddThemiaDataMigrationsPostgreSql"),
        MigrationEngine.MySql => ("Themia.Data.Migrations.MySql", "AddThemiaDataMigrationsMySql"),
        MigrationEngine.SqlServer => ("Themia.Data.Migrations.SqlServer", "AddThemiaDataMigrationsSqlServer"),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine."),
    };
}
