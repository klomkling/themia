# Themia 0.4.6 — shared FluentMigrator runner (`Themia.Data.Migrations`) (design)

> Spec. Master context: [`themia-architecture-overview.md`](../../themia-architecture-overview.md)
> (DECISION #6 — FluentMigrator as the single schema authority). Status date: 2026-06-12.

## Goal

Extract the FluentMigrator runner that currently lives — triplicated — inside the three
`Themia.Exceptional.*` provider packages into a single **neutral** package `Themia.Data.Migrations`,
and migrate Exceptional onto it. This is the foundation DECISION #6's "FluentMigrator = single schema
authority" needs: one runner that any neutral core or framework module hands its migrations to.

## Scope

This is the **foundation slice** of the FM-authority program. The user-approved decomposition:

- **0.4.6 (this spec):** the shared `Themia.Data.Migrations` runner + migrate the three Exceptional
  packages onto it. Low-risk — Exceptional's `ExceptionLogMigration` is **unchanged**; only the runner
  relocates, and Exceptional's existing 3-engine integration suites prove it end-to-end.
- **0.4.7 (deferred, own spec):** Scheduling EF→FM (its 2 tables) + persistent Quartz `AdoJobStore`
  (default-on) with `UseSystemTextJsonSerializer()` + the `qrtz_*` schema authored as per-engine FM
  migrations, all run through this runner.

**Explicitly out of 0.4.6:** anything Scheduling/Quartz; the EF concurrency-*seam* refactor (rides with
the EF MySQL provider when Pomelo ships an EF Core 10 build); framework-column / concurrency-token DDL
convention helpers (no consumer yet); the FluentMigrator 6→8 upgrade (FM 8 broke `IfDatabase` dialect
matching — FM stays **6.2.0**).

## Background (current state)

`Themia.Exceptional` (neutral core, `net8.0;net10.0`, no framework dependency) runs its schema via a
private `RunMigration` helper in `src/neutral/Themia.Exceptional/ServiceCollectionExtensions.cs`:

```csharp
private static void RunMigration(string connectionString, Action<IMigrationRunnerBuilder> configureRunner, string databaseDisplayName)
{
    using var provider = new ServiceCollection()
        .AddFluentMigratorCore()
        .ConfigureRunner(rb =>
        {
            configureRunner(rb);                         // provider passes rb.AddPostgres()/.AddMySql8()/.AddSqlServer()
            rb.WithGlobalConnectionString(connectionString)
              .ScanIn(typeof(ExceptionLogMigration).Assembly).For.Migrations();
        })
        .BuildServiceProvider(false);
    using var scope = provider.CreateScope();
    try { scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp(); }
    catch (Exception ex)
    {
        throw new InvalidOperationException(
            $"Themia.Exceptional: failed to apply the Exceptions-table migration. " +
            $"Verify the {databaseDisplayName} connection string and that the principal has DDL permissions.", ex);
    }
}
```

`AddThemiaExceptionalProvider(...)` (public, called by the three provider packages) takes a
`Action<IMigrationRunnerBuilder> configureRunner` + `databaseDisplayName` and calls `RunMigration` at DI
registration time. The three packages
(`Themia.Exceptional.{PostgreSql,MySql,SqlServer}/ServiceCollectionExtensions.cs`) are byte-for-byte
identical except `configureRunner: rb => rb.AddPostgres()` vs `.AddMySql8()` vs `.AddSqlServer()` and the
display name. `FluentMigrator`/`FluentMigrator.Runner` are pinned at `6.2.0` in `Directory.Packages.props`;
`FluentMigrator.Runner` is the meta-package that brings all engine processors, so `.AddPostgres()` /
`.AddMySql8()` / `.AddSqlServer()` all come from it.

## Architecture

### 1. New package `Themia.Data.Migrations` (neutral, `net8.0;net10.0`)

A neutral package (no `Themia.Framework.*` dependency) so both neutral cores (Exceptional) and — in
0.4.7 — framework modules (Scheduling) can consume it. References: `FluentMigrator`,
`FluentMigrator.Runner`, `Microsoft.Extensions.DependencyInjection`, `PublicApiAnalyzer`.

**`MigrationEngine` enum** — the neutral engine selector (the package cannot reference the framework's
`DatabaseProviderNames`, so it owns its own enum):

```csharp
namespace Themia.Data.Migrations;

public enum MigrationEngine
{
    Postgres,
    MySql,
    SqlServer,
}
```

**`ThemiaMigrations` static entry point** — the lifted runner, engine-agnostic:

```csharp
namespace Themia.Data.Migrations;

public static class ThemiaMigrations
{
    /// <summary>
    /// Applies all pending FluentMigrator migrations found in <paramref name="migrationAssemblies"/>
    /// against <paramref name="connectionString"/> using the <paramref name="engine"/>'s processor.
    /// Runs synchronously (MigrateUp). Throws <see cref="InvalidOperationException"/> with the engine
    /// name on failure.
    /// </summary>
    public static void Run(MigrationEngine engine, string connectionString, params Assembly[] migrationAssemblies)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(migrationAssemblies);
        if (migrationAssemblies.Length == 0)
            throw new ArgumentException("At least one migration assembly is required.", nameof(migrationAssemblies));

        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                AddProcessor(rb, engine);
                rb.WithGlobalConnectionString(connectionString)
                  .ScanIn(migrationAssemblies).For.Migrations();
            })
            .BuildServiceProvider(false);

        using var scope = provider.CreateScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Themia.Data.Migrations: failed to apply migrations against {DisplayName(engine)}. " +
                "Verify the connection string and that the principal has DDL permissions.", ex);
        }
    }

    private static void AddProcessor(IMigrationRunnerBuilder rb, MigrationEngine engine)
    {
        switch (engine)
        {
            case MigrationEngine.Postgres:  rb.AddPostgres();  break;
            case MigrationEngine.MySql:     rb.AddMySql8();     break;
            case MigrationEngine.SqlServer: rb.AddSqlServer();  break;
            default: throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine.");
        }
    }

    private static string DisplayName(MigrationEngine engine) => engine switch
    {
        MigrationEngine.Postgres  => "PostgreSQL",
        MigrationEngine.MySql     => "MySQL",
        MigrationEngine.SqlServer => "SQL Server",
        _ => engine.ToString(),
    };
}
```

The per-engine knowledge (`.AddMySql8()` vs `.AddMySql5()`, the processor selection, the display name)
is now centralized in one place. `params Assembly[]` lets a future caller (Scheduling in 0.4.7) hand
multiple migration assemblies to one runner invocation.

### 2. Exceptional onto the shared runner

**`Themia.Exceptional/ServiceCollectionExtensions.cs`:**
- Delete the private `RunMigration` method.
- Change `AddThemiaExceptionalProvider`'s signature: replace `Action<IMigrationRunnerBuilder> configureRunner`
  and `string databaseDisplayName` with `MigrationEngine engine`. When `runMigration` is true, call
  `ThemiaMigrations.Run(engine, connectionString!, typeof(ExceptionLogMigration).Assembly)`.
- Add a `ProjectReference` to `Themia.Data.Migrations`; the `using FluentMigrator.Runner;` for the
  `IMigrationRunnerBuilder` parameter is removed (no longer referenced).

New signature:
```csharp
public static IServiceCollection AddThemiaExceptionalProvider(
    this IServiceCollection services,
    IExceptionalSqlDialect dialect,
    Action<ExceptionalOptions> configure,
    MigrationEngine engine,
    string? connectionString = null,
    bool runMigration = true)
```

**The three provider packages** (`Themia.Exceptional.{PostgreSql,MySql,SqlServer}/ServiceCollectionExtensions.cs`):
- Replace `configureRunner: rb => rb.AddPostgres(), databaseDisplayName: "PostgreSQL"` with
  `engine: MigrationEngine.Postgres` (and `.MySql` / `.SqlServer` respectively).
- The adopter-facing entry points (`AddThemiaExceptionalPostgres` / `…MySql` / `…SqlServer`) keep their
  **unchanged** signatures — adopters are unaffected.
- csproj: drop the direct `<PackageReference Include="FluentMigrator.Runner" />` (now transitive via
  `Themia.Data.Migrations`) and add `<ProjectReference Include="../Themia.Data.Migrations/…csproj" />`.
  Keep the engine driver ref (`Npgsql` / `MySqlConnector` / `Microsoft.Data.SqlClient`) — that serves the
  Exceptional store, not the runner.

**PublicAPI:** `AddThemiaExceptionalProvider`'s changed signature is a public-API change on a
provider-author extension point (recorded in `Themia.Exceptional`'s PublicAPI files — `*REMOVED*` the old
line, add the new). The adopter-facing `AddThemiaExceptional*` surfaces are unchanged. New package
`Themia.Data.Migrations` gets empty `PublicAPI.Shipped.txt` + populated `Unshipped.txt`
(`MigrationEngine` + members, `ThemiaMigrations` + `Run`).

### 3. Package management & solution

- `Directory.Packages.props`: no new versions needed — `FluentMigrator` + `FluentMigrator.Runner` 6.2.0
  already present; `Themia.Data.Migrations` references them by name.
- `Themia.sln`: add `src/neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj` and its test project.

## Testing

- **Exceptional's three integration suites** (`Themia.Exceptional.{PostgreSql,MySql,SqlServer}.IntegrationTests`,
  Testcontainers) stay green unchanged — they already run `AddThemiaExceptional*` → the runner → `MigrateUp`
  creates the `Exceptions` table, then the store round-trips. This is the end-to-end proof the extraction
  preserved behavior across all three engines.
- **New `Themia.Data.Migrations` tests:**
  - Unit: `MigrationEngine` → processor/display-name mapping is exhaustive; `Run` guards (null/empty
    connection string, no assemblies) throw the documented exceptions.
  - Integration (one engine, Testcontainers Postgres — cheapest): a trivial test migration assembly
    applied via `ThemiaMigrations.Run(MigrationEngine.Postgres, …)` creates its table; a second run is a
    no-op (idempotent `MigrateUp`); a bad connection string surfaces the wrapped `InvalidOperationException`
    naming the engine.

## Release

- `Directory.Build.props` `<Version>` `0.4.5` → `0.4.6`.
- `CHANGELOG.md` `## 0.4.6`:
  - **Added** — `Themia.Data.Migrations`: a neutral shared FluentMigrator runner
    (`ThemiaMigrations.Run(engine, connectionString, …assemblies)`).
  - **Changed** — the `Themia.Exceptional.*` packages now run their schema migration through the shared
    runner (internal refactor; the adopter-facing `AddThemiaExceptional*` API is unchanged). The
    provider-author `AddThemiaExceptionalProvider` extension now takes a `MigrationEngine` instead of a
    runner-builder callback.
- `MIGRATION.md`: a note only for the niche case of a third party calling `AddThemiaExceptionalProvider`
  directly (pass `MigrationEngine.X` instead of `configureRunner`/`databaseDisplayName`).
- New `src/neutral/Themia.Data.Migrations/` package: `.csproj` (net8.0;net10.0), `PublicAPI.{Shipped,Unshipped}.txt`,
  `PublicApiAnalyzer` ref, `InternalsVisibleTo` for its test project if needed.

## Success criteria

- `Themia.Data.Migrations` exists as a neutral net8.0;net10.0 package; `ThemiaMigrations.Run` applies
  migrations for all three engines via one code path.
- The three Exceptional provider packages contain no FluentMigrator runner wiring of their own — they pass
  a `MigrationEngine` and delegate; the byte-for-byte triplication is gone.
- All three Exceptional integration suites green (behavior preserved); new runner tests green.
- Clean `dotnet build Themia.sln --no-incremental` (TreatWarningsAsErrors, PublicAPI recorded).
