# Themia 0.4.6 — shared FluentMigrator runner (`Themia.Data.Migrations`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the triplicated FluentMigrator runner from the three `Themia.Exceptional.*` provider packages into a single neutral `Themia.Data.Migrations` package, then migrate Exceptional onto it.

**Architecture:** A new neutral package (`net8.0;net10.0`, no framework dependency) exposes `MigrationEngine` (Postgres/MySql/SqlServer) and `ThemiaMigrations.Run(engine, connectionString, params Assembly[])`, which lifts Exceptional's private `RunMigration` verbatim and centralizes the per-engine processor selection. `Themia.Exceptional`'s `AddThemiaExceptionalProvider` drops its `Action<IMigrationRunnerBuilder> configureRunner` + `databaseDisplayName` parameters in favor of a single `MigrationEngine engine`, delegating to the shared runner. The three provider packages pass `MigrationEngine.X` and drop their direct `FluentMigrator.Runner` reference. Exceptional's `ExceptionLogMigration` is unchanged; its three Testcontainers integration suites prove the extraction end-to-end.

**Tech Stack:** .NET 8/.NET 10, FluentMigrator 6.2.0 (`FluentMigrator` + `FluentMigrator.Runner`, both already pinned), `Microsoft.Extensions.DependencyInjection`, xUnit, Testcontainers.PostgreSql, `Microsoft.CodeAnalysis.PublicApiAnalyzers`.

**Reference spec:** `docs/superpowers/specs/2026-06-12-themia-data-migrations-runner-design.md`

---

## File Structure

**New — `src/neutral/Themia.Data.Migrations/`:**
- `Themia.Data.Migrations.csproj` — neutral package (net8.0;net10.0), refs FluentMigrator + FluentMigrator.Runner + Microsoft.Extensions.DependencyInjection + PublicApiAnalyzer.
- `MigrationEngine.cs` — the neutral engine selector enum.
- `ThemiaMigrations.cs` — the static `Run` entry point (lifted runner).
- `PublicAPI.Shipped.txt` (empty) / `PublicAPI.Unshipped.txt` (the new surface).

**New — `tests/Themia.Data.Migrations.Tests/`:**
- `Themia.Data.Migrations.Tests.csproj` — unit tests (net8.0;net10.0), no DB.
- `ThemiaMigrationsGuardTests.cs` — guard-clause + unknown-engine behavior.

**New — `tests/Themia.Data.Migrations.IntegrationTests/`:**
- `Themia.Data.Migrations.IntegrationTests.csproj` — Testcontainers Postgres (net10.0).
- `ProbeMigration.cs` — a trivial engine-agnostic test migration.
- `ThemiaMigrationsPostgresTests.cs` — apply / idempotent / bad-connection-names-engine.

**Modified — Exceptional onto the shared runner:**
- `src/neutral/Themia.Exceptional/ServiceCollectionExtensions.cs` — delete `RunMigration`, change `AddThemiaExceptionalProvider` signature, delegate to `ThemiaMigrations.Run`.
- `src/neutral/Themia.Exceptional/Themia.Exceptional.csproj` — add ProjectReference to the new package, drop direct `FluentMigrator.Runner`.
- `src/neutral/Themia.Exceptional/PublicAPI.Unshipped.txt` — replace the `AddThemiaExceptionalProvider` line.
- `src/neutral/Themia.Exceptional.{PostgreSql,MySql,SqlServer}/ServiceCollectionExtensions.cs` — pass `engine:`, drop `using FluentMigrator.Runner;`.
- `src/neutral/Themia.Exceptional.{PostgreSql,MySql,SqlServer}/*.csproj` — drop `FluentMigrator.Runner`, add ProjectReference to the new package.

**Modified — release wiring:**
- `Directory.Build.props` — `<Version>` `0.4.5` → `0.4.6`.
- `CHANGELOG.md` — `## 0.4.6` section.
- `MIGRATION.md` — `## 0.4.6` note (third-party `AddThemiaExceptionalProvider` callers only).
- `Themia.sln` — three new projects (via `dotnet sln add`).

---

## Task 1: Scaffold the `Themia.Data.Migrations` package and unit-test project

Create the empty project skeletons and wire them into the solution so later test-first tasks have something to reference. No behavior yet.

**Files:**
- Create: `src/neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj`
- Create: `src/neutral/Themia.Data.Migrations/PublicAPI.Shipped.txt`
- Create: `src/neutral/Themia.Data.Migrations/PublicAPI.Unshipped.txt`
- Create: `tests/Themia.Data.Migrations.Tests/Themia.Data.Migrations.Tests.csproj`
- Modify: `Themia.sln`

- [ ] **Step 1: Create the source project file**

`src/neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Data.Migrations</PackageId>
    <Description>Framework-neutral FluentMigrator runner: applies migrations for PostgreSQL, MySQL, and SQL Server through one engine-agnostic entry point.</Description>
    <!-- Version is inherited from Directory.Build.props (shared). Do not set it here. -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FluentMigrator" />
    <PackageReference Include="FluentMigrator.Runner" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the PublicAPI files**

`src/neutral/Themia.Data.Migrations/PublicAPI.Shipped.txt`:

```
#nullable enable
```

`src/neutral/Themia.Data.Migrations/PublicAPI.Unshipped.txt`:

```
#nullable enable
```

- [ ] **Step 3: Create the unit-test project file**

`tests/Themia.Data.Migrations.Tests/Themia.Data.Migrations.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Add both projects to the solution**

Run:
```bash
dotnet sln Themia.sln add src/neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj
dotnet sln Themia.sln add tests/Themia.Data.Migrations.Tests/Themia.Data.Migrations.Tests.csproj
```
Expected: `Project ... added to the solution.` twice.

- [ ] **Step 5: Build to confirm the skeleton compiles**

Run: `dotnet build Themia.sln`
Expected: PASS (the new projects build empty; no warnings — PublicAPI files present so RS0016 has nothing to report yet).

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Data.Migrations tests/Themia.Data.Migrations.Tests Themia.sln
git commit -m "chore: scaffold Themia.Data.Migrations package and unit-test project"
```

---

## Task 2: Implement `MigrationEngine` + `ThemiaMigrations.Run` (guards, TDD)

Write the guard-clause unit tests first, then the enum and runner. The guard paths and the unknown-engine `default` branch are all reachable without a database (the processor-selection lambda runs synchronously during `ConfigureRunner`, before any connection).

**Files:**
- Test: `tests/Themia.Data.Migrations.Tests/ThemiaMigrationsGuardTests.cs`
- Create: `src/neutral/Themia.Data.Migrations/MigrationEngine.cs`
- Create: `src/neutral/Themia.Data.Migrations/ThemiaMigrations.cs`
- Modify: `src/neutral/Themia.Data.Migrations/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Write the failing guard tests**

`tests/Themia.Data.Migrations.Tests/ThemiaMigrationsGuardTests.cs`:

```csharp
using System.Reflection;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Data.Migrations.Tests;

public class ThemiaMigrationsGuardTests
{
    private static readonly Assembly[] OneAssembly = [typeof(ThemiaMigrationsGuardTests).Assembly];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Run_ShouldThrowArgumentException_WhenConnectionStringIsNullOrWhitespace(string? connectionString)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, connectionString!, OneAssembly));
    }

    [Fact]
    public void Run_ShouldThrowArgumentNullException_WhenAssembliesArrayIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, "Host=localhost", null!));
    }

    [Fact]
    public void Run_ShouldThrowArgumentException_WhenNoAssembliesProvided()
    {
        Assert.Throws<ArgumentException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, "Host=localhost"));
    }

    [Fact]
    public void Run_ShouldThrowArgumentOutOfRangeException_WhenEngineIsUnknown()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThemiaMigrations.Run((MigrationEngine)999, "Host=localhost", OneAssembly));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Themia.Data.Migrations.Tests/Themia.Data.Migrations.Tests.csproj`
Expected: FAIL — compile error, `MigrationEngine` and `ThemiaMigrations` do not exist.

- [ ] **Step 3: Implement the enum**

`src/neutral/Themia.Data.Migrations/MigrationEngine.cs`:

```csharp
namespace Themia.Data.Migrations;

/// <summary>
/// The database engine whose FluentMigrator processor a migration run targets.
/// Neutral selector owned by this package (it cannot reference the framework's provider names).
/// </summary>
public enum MigrationEngine
{
    /// <summary>PostgreSQL (FluentMigrator <c>AddPostgres</c>).</summary>
    Postgres,

    /// <summary>MySQL / MariaDB (FluentMigrator <c>AddMySql8</c>).</summary>
    MySql,

    /// <summary>SQL Server (FluentMigrator <c>AddSqlServer</c>).</summary>
    SqlServer,
}
```

- [ ] **Step 4: Implement the runner**

`src/neutral/Themia.Data.Migrations/ThemiaMigrations.cs`:

```csharp
using System.Reflection;
using FluentMigrator.Runner;
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
    /// <param name="engine">The target database engine.</param>
    /// <param name="connectionString">Connection string for the migration runner. Required.</param>
    /// <param name="migrationAssemblies">One or more assemblies scanned for <c>[Migration]</c> types. At least one is required.</param>
    /// <exception cref="ArgumentException">The connection string is null/whitespace, or no assemblies were supplied.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="migrationAssemblies"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="engine"/> is not a known engine.</exception>
    /// <exception cref="InvalidOperationException">The migration failed to apply; the message names the engine.</exception>
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
            case MigrationEngine.Postgres: rb.AddPostgres(); break;
            case MigrationEngine.MySql: rb.AddMySql8(); break;
            case MigrationEngine.SqlServer: rb.AddSqlServer(); break;
            default: throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown migration engine.");
        }
    }

    private static string DisplayName(MigrationEngine engine) => engine switch
    {
        MigrationEngine.Postgres => "PostgreSQL",
        MigrationEngine.MySql => "MySQL",
        MigrationEngine.SqlServer => "SQL Server",
        _ => engine.ToString(),
    };
}
```

- [ ] **Step 5: Record the new public API**

`src/neutral/Themia.Data.Migrations/PublicAPI.Unshipped.txt`:

```
#nullable enable
Themia.Data.Migrations.MigrationEngine
Themia.Data.Migrations.MigrationEngine.MySql = 1 -> Themia.Data.Migrations.MigrationEngine
Themia.Data.Migrations.MigrationEngine.Postgres = 0 -> Themia.Data.Migrations.MigrationEngine
Themia.Data.Migrations.MigrationEngine.SqlServer = 2 -> Themia.Data.Migrations.MigrationEngine
Themia.Data.Migrations.ThemiaMigrations
static Themia.Data.Migrations.ThemiaMigrations.Run(Themia.Data.Migrations.MigrationEngine engine, string! connectionString, params System.Reflection.Assembly![]! migrationAssemblies) -> void
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Themia.Data.Migrations.Tests/Themia.Data.Migrations.Tests.csproj`
Expected: PASS (6 cases).

If the build reports `RS0016` (unshipped API mismatch), copy the analyzer's exact suggested line into `PublicAPI.Unshipped.txt` and re-run — the analyzer's text is authoritative over the hand-written lines above.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Data.Migrations tests/Themia.Data.Migrations.Tests
git commit -m "feat: add ThemiaMigrations.Run shared FluentMigrator runner"
```

---

## Task 3: Integration test — apply a real migration through the runner (TDD, Testcontainers Postgres)

Prove `ThemiaMigrations.Run` applies migrations against a live engine, is idempotent on re-run, and surfaces a wrapped error naming the engine. Postgres is the cheapest container.

**Files:**
- Create: `tests/Themia.Data.Migrations.IntegrationTests/Themia.Data.Migrations.IntegrationTests.csproj`
- Create: `tests/Themia.Data.Migrations.IntegrationTests/ProbeMigration.cs`
- Test: `tests/Themia.Data.Migrations.IntegrationTests/ThemiaMigrationsPostgresTests.cs`
- Modify: `Themia.sln`

- [ ] **Step 1: Create the integration-test project file**

`tests/Themia.Data.Migrations.IntegrationTests/Themia.Data.Migrations.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="FluentMigrator" />
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Testcontainers.PostgreSql" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the trivial test migration**

`tests/Themia.Data.Migrations.IntegrationTests/ProbeMigration.cs`:

```csharp
using FluentMigrator;

namespace Themia.Data.Migrations.IntegrationTests;

/// <summary>A trivial engine-agnostic migration used only to prove the runner applies migrations.</summary>
[Migration(202606120001, "Themia.Data.Migrations probe table")]
public sealed class ProbeMigration : Migration
{
    public override void Up() =>
        Create.Table("migrations_probe").WithColumn("Id").AsInt32().PrimaryKey();

    public override void Down() => Delete.Table("migrations_probe");
}
```

- [ ] **Step 3: Write the failing integration tests**

`tests/Themia.Data.Migrations.IntegrationTests/ThemiaMigrationsPostgresTests.cs`:

```csharp
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Data.Migrations.IntegrationTests;

[Trait("Category", "Integration")]
public class ThemiaMigrationsPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Run_CreatesTheMigratedTable()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(ProbeMigration).Assembly);

        Assert.True(await TableExistsAsync("migrations_probe"));
    }

    [Fact]
    public void Run_IsIdempotent_WhenInvokedTwice()
    {
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(ProbeMigration).Assembly);
        var second = Record.Exception(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(ProbeMigration).Assembly));

        Assert.Null(second);
    }

    [Fact]
    public void Run_WrapsFailure_NamingTheEngine()
    {
        const string badConn = "Host=127.0.0.1;Port=1;Username=x;Password=y;Database=z;Timeout=2;Command Timeout=2";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, badConn, typeof(ProbeMigration).Assembly));

        Assert.Contains("PostgreSQL", ex.Message);
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var conn = new NpgsqlConnection(ConnString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("name", table);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
```

- [ ] **Step 4: Add the project to the solution**

Run:
```bash
dotnet sln Themia.sln add tests/Themia.Data.Migrations.IntegrationTests/Themia.Data.Migrations.IntegrationTests.csproj
```
Expected: `Project ... added to the solution.`

- [ ] **Step 5: Run the integration tests to verify they pass**

Run: `dotnet test tests/Themia.Data.Migrations.IntegrationTests/Themia.Data.Migrations.IntegrationTests.csproj`
Expected: PASS (3 cases). Requires a running Docker daemon; if Docker is unavailable locally these run in CI — note that in the commit if skipped.

- [ ] **Step 6: Commit**

```bash
git add tests/Themia.Data.Migrations.IntegrationTests Themia.sln
git commit -m "test: integration-test ThemiaMigrations.Run against Postgres"
```

---

## Task 4: Migrate Exceptional onto the shared runner

Atomic change: the new `AddThemiaExceptionalProvider` signature ripples into all three provider packages, so core and the three providers must change together to keep `dotnet build Themia.sln` green. Exceptional's `ExceptionLogMigration` and the adopter-facing `AddThemiaExceptional*` signatures are untouched, so the existing integration suites are the regression proof.

**Files:**
- Modify: `src/neutral/Themia.Exceptional/ServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Exceptional/Themia.Exceptional.csproj`
- Modify: `src/neutral/Themia.Exceptional/PublicAPI.Unshipped.txt`
- Modify: `src/neutral/Themia.Exceptional.PostgreSql/ServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Exceptional.PostgreSql/Themia.Exceptional.PostgreSql.csproj`
- Modify: `src/neutral/Themia.Exceptional.MySql/ServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Exceptional.MySql/Themia.Exceptional.MySql.csproj`
- Modify: `src/neutral/Themia.Exceptional.SqlServer/ServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Exceptional.SqlServer/Themia.Exceptional.SqlServer.csproj`

- [ ] **Step 1: Rewrite the Exceptional core registration**

Replace the entire contents of `src/neutral/Themia.Exceptional/ServiceCollectionExtensions.cs` with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Data.Migrations;
using Themia.Exceptional.Migrations;
using Themia.Exceptional.Serilog;

namespace Themia.Exceptional;

/// <summary>Shared registration used by provider packages (e.g. Themia.Exceptional.PostgreSql).</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the exception store over a provider-supplied <see cref="IExceptionalSqlDialect"/> plus validated options.
    /// Provider packages call this after registering their dialect.
    /// </summary>
    public static IServiceCollection AddThemiaExceptionalCore(this IServiceCollection services, Action<ExceptionalOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ExceptionalOptions();
        configure(options);
        options.Validate();
        services.TryAddSingleton(options);

        services.TryAddSingleton<IExceptionStore>(sp =>
            new ExceptionStoreEngine(sp.GetRequiredService<IExceptionalSqlDialect>(), options));

        return services;
    }

    /// <summary>
    /// Registers a complete provider-backed exception store: the <paramref name="dialect"/>, the core
    /// engine/options, the Serilog sink + HTTP-context enricher singletons, and (unless
    /// <paramref name="runMigration"/> is <see langword="false"/>) runs the FluentMigrator schema migration
    /// immediately so the <c>Exceptions</c> table exists.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="dialect">The provider dialect (already carries its connection string).</param>
    /// <param name="configure">Required options callback; <see cref="ExceptionalOptions.ApplicationName"/> is validated at startup.</param>
    /// <param name="engine">The database engine whose FluentMigrator processor applies the schema migration.</param>
    /// <param name="connectionString">Connection string passed to the migration runner. Required when <paramref name="runMigration"/> is <see langword="true"/>.</param>
    /// <param name="runMigration">When <see langword="true"/> (default), runs the schema migration immediately.</param>
    public static IServiceCollection AddThemiaExceptionalProvider(
        this IServiceCollection services,
        IExceptionalSqlDialect dialect,
        Action<ExceptionalOptions> configure,
        MigrationEngine engine,
        string? connectionString = null,
        bool runMigration = true)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(configure);

        services.TryAddSingleton<IExceptionalSqlDialect>(dialect);
        services.AddThemiaExceptionalCore(configure);

        services.AddHttpContextAccessor();
        services.TryAddSingleton<HttpContextEnricher>();
        services.TryAddSingleton<ExceptionalSerilogSink>(sp =>
            new ExceptionalSerilogSink(
                sp.GetRequiredService<IExceptionStore>(),
                sp.GetRequiredService<ExceptionalOptions>()));

        if (runMigration)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ThemiaMigrations.Run(engine, connectionString, typeof(ExceptionLogMigration).Assembly);
        }

        return services;
    }
}
```

- [ ] **Step 2: Update the Exceptional core csproj**

In `src/neutral/Themia.Exceptional/Themia.Exceptional.csproj`, remove the `FluentMigrator.Runner` package reference (now transitive via the new ProjectReference and no longer used directly):

```xml
    <PackageReference Include="FluentMigrator.Runner" />
```

Keep `<PackageReference Include="FluentMigrator" />` (used by `ExceptionLogMigration`). Then add a ProjectReference. Add this `ItemGroup` next to the existing references:

```xml
  <ItemGroup>
    <ProjectReference Include="../Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
  </ItemGroup>
```

- [ ] **Step 3: Update the Exceptional core PublicAPI**

In `src/neutral/Themia.Exceptional/PublicAPI.Unshipped.txt`, replace the old `AddThemiaExceptionalProvider` line:

```
static Themia.Exceptional.ServiceCollectionExtensions.AddThemiaExceptionalProvider(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Themia.Exceptional.IExceptionalSqlDialect! dialect, System.Action<Themia.Exceptional.ExceptionalOptions!>! configure, System.Action<FluentMigrator.Runner.IMigrationRunnerBuilder!>! configureRunner, string! databaseDisplayName, string? connectionString = null, bool runMigration = true) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

with the new signature:

```
static Themia.Exceptional.ServiceCollectionExtensions.AddThemiaExceptionalProvider(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Themia.Exceptional.IExceptionalSqlDialect! dialect, System.Action<Themia.Exceptional.ExceptionalOptions!>! configure, Themia.Data.Migrations.MigrationEngine engine, string? connectionString = null, bool runMigration = true) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

- [ ] **Step 4: Update the PostgreSQL provider call site**

Replace the entire contents of `src/neutral/Themia.Exceptional.PostgreSql/ServiceCollectionExtensions.cs` with (drops `using FluentMigrator.Runner;`, adds `using Themia.Data.Migrations;`, passes `engine:`):

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Exceptional;

namespace Themia.Exceptional.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed Themia exception store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL exception store: dialect, engine, options, and runs the
    /// FluentMigrator schema migration immediately so the <c>Exceptions</c> table exists.
    /// <para>
    /// Also registers <see cref="Themia.Exceptional.Serilog.ExceptionalSerilogSink"/> and
    /// <see cref="Themia.Exceptional.Serilog.HttpContextEnricher"/>
    /// as singletons in the DI container <strong>for the host to wire into its own Serilog
    /// <c>LoggerConfiguration</c></strong>. This package does not configure the global logger.
    /// The host should resolve and attach them, for example:
    /// <code>
    /// .Enrich.With(sp.GetRequiredService&lt;HttpContextEnricher&gt;())
    /// .WriteTo.Sink(sp.GetRequiredService&lt;ExceptionalSerilogSink&gt;())
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="configure">
    /// Required configuration callback. <see cref="ExceptionalOptions.ApplicationName"/> is mandatory and
    /// validated at startup, so this cannot be omitted.
    /// </param>
    public static IServiceCollection AddThemiaExceptionalPostgres(
        this IServiceCollection services, string connectionString, Action<ExceptionalOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddThemiaExceptionalProvider(
            dialect: new PostgresExceptionalDialect(connectionString),
            configure: configure,
            engine: MigrationEngine.Postgres,
            connectionString: connectionString);
    }
}
```

- [ ] **Step 5: Update the PostgreSQL provider csproj**

In `src/neutral/Themia.Exceptional.PostgreSql/Themia.Exceptional.PostgreSql.csproj`, remove:

```xml
    <PackageReference Include="FluentMigrator.Runner" />
```

and add a ProjectReference inside the existing `ItemGroup` that already references `../Themia.Exceptional/Themia.Exceptional.csproj`:

```xml
    <ProjectReference Include="../Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
```

- [ ] **Step 6: Update the MySQL provider call site**

Replace the entire contents of `src/neutral/Themia.Exceptional.MySql/ServiceCollectionExtensions.cs` with the same shape — swap `using FluentMigrator.Runner;` for `using Themia.Data.Migrations;`, keep the MySQL-specific XML doc on `connectionString`, and replace the `AddThemiaExceptionalProvider` call's `configureRunner`/`databaseDisplayName` with `engine: MigrationEngine.MySql`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Exceptional;

namespace Themia.Exceptional.MySql;

/// <summary>DI entry point for the MySQL-backed Themia exception store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MySQL exception store: dialect, engine, options, and runs the
    /// FluentMigrator schema migration immediately so the <c>Exceptions</c> table exists.
    /// <para>
    /// Also registers <see cref="Themia.Exceptional.Serilog.ExceptionalSerilogSink"/> and
    /// <see cref="Themia.Exceptional.Serilog.HttpContextEnricher"/>
    /// as singletons in the DI container <strong>for the host to wire into its own Serilog
    /// <c>LoggerConfiguration</c></strong>. This package does not configure the global logger.
    /// The host should resolve and attach them, for example:
    /// <code>
    /// .Enrich.With(sp.GetRequiredService&lt;HttpContextEnricher&gt;())
    /// .WriteTo.Sink(sp.GetRequiredService&lt;ExceptionalSerilogSink&gt;())
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">
    /// MySQL connection string. The dialect always pins <c>GuidFormat=Char36</c> on its own connections (the
    /// <c>Guid</c> column is <c>CHAR(36)</c>), so callers need not — and any conflicting <c>GuidFormat</c>/
    /// <c>OldGuids</c> is overridden to keep <see cref="System.Guid"/> lookups correct.
    /// </param>
    /// <param name="configure">
    /// Required configuration callback. <see cref="ExceptionalOptions.ApplicationName"/> is mandatory and
    /// validated at startup, so this cannot be omitted.
    /// </param>
    public static IServiceCollection AddThemiaExceptionalMySql(
        this IServiceCollection services, string connectionString, Action<ExceptionalOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddThemiaExceptionalProvider(
            dialect: new MySqlExceptionalDialect(connectionString),
            configure: configure,
            engine: MigrationEngine.MySql,
            connectionString: connectionString);
    }
}
```

- [ ] **Step 7: Update the MySQL provider csproj**

In `src/neutral/Themia.Exceptional.MySql/Themia.Exceptional.MySql.csproj`, remove the `FluentMigrator.Runner` package reference and add the ProjectReference next to the existing `../Themia.Exceptional/Themia.Exceptional.csproj` reference:

```xml
    <ProjectReference Include="../Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
```

- [ ] **Step 8: Update the SQL Server provider call site**

Replace the entire contents of `src/neutral/Themia.Exceptional.SqlServer/ServiceCollectionExtensions.cs` with:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Exceptional;

namespace Themia.Exceptional.SqlServer;

/// <summary>DI entry point for the SQL Server-backed Themia exception store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server exception store: dialect, engine, options, and runs the
    /// FluentMigrator schema migration immediately so the <c>Exceptions</c> table exists.
    /// <para>
    /// Also registers <see cref="Themia.Exceptional.Serilog.ExceptionalSerilogSink"/> and
    /// <see cref="Themia.Exceptional.Serilog.HttpContextEnricher"/>
    /// as singletons in the DI container <strong>for the host to wire into its own Serilog
    /// <c>LoggerConfiguration</c></strong>. This package does not configure the global logger.
    /// The host should resolve and attach them, for example:
    /// <code>
    /// .Enrich.With(sp.GetRequiredService&lt;HttpContextEnricher&gt;())
    /// .WriteTo.Sink(sp.GetRequiredService&lt;ExceptionalSerilogSink&gt;())
    /// </code>
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="configure">
    /// Required configuration callback. <see cref="ExceptionalOptions.ApplicationName"/> is mandatory and
    /// validated at startup, so this cannot be omitted.
    /// </param>
    public static IServiceCollection AddThemiaExceptionalSqlServer(
        this IServiceCollection services, string connectionString, Action<ExceptionalOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configure);

        return services.AddThemiaExceptionalProvider(
            dialect: new SqlServerExceptionalDialect(connectionString),
            configure: configure,
            engine: MigrationEngine.SqlServer,
            connectionString: connectionString);
    }
}
```

- [ ] **Step 9: Update the SQL Server provider csproj**

In `src/neutral/Themia.Exceptional.SqlServer/Themia.Exceptional.SqlServer.csproj`, remove the `FluentMigrator.Runner` package reference and add the ProjectReference next to the existing `../Themia.Exceptional/Themia.Exceptional.csproj` reference:

```xml
    <ProjectReference Include="../Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
```

- [ ] **Step 10: Clean build to verify the refactor and PublicAPI**

Run: `dotnet build Themia.sln --no-incremental`
Expected: PASS, zero warnings (TreatWarningsAsErrors). If `RS0016`/`RS0017` fires on `Themia.Exceptional`, reconcile `PublicAPI.Unshipped.txt` with the analyzer's exact suggested text and rebuild.

- [ ] **Step 11: Run the Exceptional integration suites (regression proof)**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Exceptional"`
Expected: PASS — all three engine suites (`Postgres`/`MySql`/`SqlServer`) still create the `Exceptions` table through the relocated runner and round-trip the store. Requires Docker; if unavailable locally, rely on CI and note the skip in the commit.

- [ ] **Step 12: Commit**

```bash
git add src/neutral/Themia.Exceptional src/neutral/Themia.Exceptional.PostgreSql src/neutral/Themia.Exceptional.MySql src/neutral/Themia.Exceptional.SqlServer
git commit -m "refactor: run Themia.Exceptional schema via shared Themia.Data.Migrations runner"
```

---

## Task 5: Release wiring (version, CHANGELOG, MIGRATION)

**Files:**
- Modify: `Directory.Build.props:26`
- Modify: `CHANGELOG.md`
- Modify: `MIGRATION.md`

- [ ] **Step 1: Bump the shared version**

In `Directory.Build.props`, change:

```xml
    <Version>0.4.5</Version>
```

to:

```xml
    <Version>0.4.6</Version>
```

- [ ] **Step 2: Add the CHANGELOG entry**

In `CHANGELOG.md`, immediately below the `## [Unreleased]` line, insert:

```markdown

## 0.4.6 — 2026-06-12

Foundation slice of the FluentMigrator-authority program (DECISION #6): the FluentMigrator runner that
was triplicated inside the three `Themia.Exceptional.*` provider packages becomes one neutral package
that any neutral core or framework module can hand its migrations to.

### Added

- **`Themia.Data.Migrations`** — a neutral (`net8.0;net10.0`) shared FluentMigrator runner.
  `ThemiaMigrations.Run(MigrationEngine engine, string connectionString, params Assembly[] migrationAssemblies)`
  selects the engine's processor (`Postgres`/`MySql`/`SqlServer`), scans the supplied assemblies, and
  applies pending migrations (`MigrateUp`), wrapping failures in an `InvalidOperationException` that names
  the engine.

### Changed

- The `Themia.Exceptional.*` packages now apply their schema migration through the shared runner instead
  of each carrying an identical inline runner. The adopter-facing `AddThemiaExceptional{Postgres,MySql,SqlServer}`
  API is unchanged. The provider-author extension `AddThemiaExceptionalProvider` now takes a
  `MigrationEngine` instead of an `Action<IMigrationRunnerBuilder>` + display-name pair.
```

- [ ] **Step 3: Add the MIGRATION note**

In `MIGRATION.md`, immediately below the `## How to read this guide` section's content (above `## 0.4.5`), insert:

```markdown
## 0.4.6

### `AddThemiaExceptionalProvider` takes a `MigrationEngine`

**What changed:** the provider-author extension `AddThemiaExceptionalProvider` (in `Themia.Exceptional`)
replaced its `Action<IMigrationRunnerBuilder> configureRunner` + `string databaseDisplayName` parameters
with a single `Themia.Data.Migrations.MigrationEngine engine`.

**Why:** the FluentMigrator runner moved into the neutral `Themia.Data.Migrations` package so every
neutral core and framework module shares one runner (DECISION #6). The engine enum replaces the
per-call runner-builder callback.

**Who is affected:** only third parties that call `AddThemiaExceptionalProvider` directly to back a
custom dialect. Adopters using `AddThemiaExceptionalPostgres` / `…MySql` / `…SqlServer` are unaffected.

**How to upgrade:**

- Before:
  ```csharp
  services.AddThemiaExceptionalProvider(
      dialect: myDialect,
      configure: opt => opt.ApplicationName = "App",
      configureRunner: rb => rb.AddPostgres(),
      connectionString: connString,
      databaseDisplayName: "PostgreSQL");
  ```
- After:
  ```csharp
  using Themia.Data.Migrations;

  services.AddThemiaExceptionalProvider(
      dialect: myDialect,
      configure: opt => opt.ApplicationName = "App",
      engine: MigrationEngine.Postgres,
      connectionString: connString);
  ```
```

- [ ] **Step 4: Final clean build**

Run: `dotnet build Themia.sln --no-incremental`
Expected: PASS, zero warnings.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props CHANGELOG.md MIGRATION.md
git commit -m "chore: release 0.4.6 — shared Themia.Data.Migrations runner"
```

---

## Notes for the executor

- **FluentMigrator stays 6.2.0.** Do not touch `Directory.Packages.props` — `FluentMigrator` and
  `FluentMigrator.Runner` 6.2.0 are already pinned; the new package references them by name.
- **No `dotnet ef migrations add`** anywhere (DECISION #6 — FluentMigrator owns DDL).
- **PublicAPI text is authoritative from the analyzer.** Where this plan hand-writes `PublicAPI.Unshipped.txt`
  lines, a clean build's `RS0016`/`RS0017` diagnostic is the source of truth — paste the analyzer's exact
  suggested line if it differs.
- **The uncommitted spec + roadmap-reconcile edits** already on `main` (the new 0.4.6 spec and edits to the
  arch overview, 0.4.5 spec, and release-strategy spec) should be committed alongside this work on the
  feature branch before opening the PR.
```
