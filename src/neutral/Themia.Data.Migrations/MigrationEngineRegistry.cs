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
    /// <param name="adapter">The adapter to register.</param>
    /// <exception cref="ArgumentNullException"><paramref name="adapter"/> is <see langword="null"/>.</exception>
    public static void Add(IMigrationEngineAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        Adapters[adapter.Engine] = adapter;
    }

    /// <summary>
    /// Removes every registered adapter. Test-only: production startup code has no reason to call this,
    /// since registration is meant to accumulate as packages are added, never to be undone.
    /// </summary>
    public static void Reset() => Adapters.Clear();

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
