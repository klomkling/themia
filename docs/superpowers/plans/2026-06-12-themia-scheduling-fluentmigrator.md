# Scheduling EF→FluentMigrator (Postgres + SQL Server) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move `Themia.Modules.Scheduling`'s two tables off EF Core migrations onto a FluentMigrator migration applied through the shared `ThemiaMigrations.Run` runner, and make the module provider-agnostic over PostgreSQL and SQL Server.

**Architecture:** A new `SchedulingSchemaMigration` (FluentMigrator, `IfDatabase("postgres","sqlserver")` + unsupported-engine guard, mirroring `ExceptionLogMigration`) replaces the EF migration. `SchedulingModule` resolves the app's registered `IDatabaseProvider` to (a) configure `SchedulingDbContext` for the active EF provider on the `Default` connection and (b) map `providerName → MigrationEngine` for the runner. `InitializeAsync` calls `ThemiaMigrations.Run` instead of `context.Database.MigrateAsync()`. EF-migration artifacts are deleted.

**Tech Stack:** .NET 10, EF Core 10 (Npgsql + SqlServer providers), FluentMigrator 6.2.0 via `Themia.Data.Migrations`, xUnit, Testcontainers (PostgreSql + MsSql).

**Reference spec:** `docs/superpowers/specs/2026-06-12-themia-scheduling-fluentmigrator-design.md`

---

## File Structure

**New:**
- `src/modules/Themia.Modules.Scheduling/Migrations/SchedulingSchemaMigration.cs` — the FM migration (replaces the EF one).
- `tests/Themia.Modules.Scheduling.IntegrationTests/FakeDatabaseProvider.cs` — test `IDatabaseProvider` supplying a provider name.

**Modified:**
- `src/framework/Themia.Framework.Data.EFCore/Extensions/ServiceCollectionExtensions.cs` — register the active `IDatabaseProvider` in DI (`TryAddSingleton`).
- `src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj` — +`Themia.Data.Migrations` ProjectRef, +`Microsoft.EntityFrameworkCore.SqlServer`, −`Microsoft.EntityFrameworkCore.Design`.
- `src/modules/Themia.Modules.Scheduling/SchedulingModule.cs` — provider-agnostic registration + `ToMigrationEngine` + `InitializeAsync` via the runner + updated class docs.
- `tests/Themia.Modules.Scheduling.IntegrationTests/SchedulingModuleTests.cs` — multi-engine base + PG/SqlServer derived; table-exists assertion replaces `GetPendingMigrationsAsync`.
- `tests/Themia.Modules.Scheduling.IntegrationTests/Themia.Modules.Scheduling.IntegrationTests.csproj` — +`Testcontainers.MsSql`.
- `Directory.Build.props` (version), `CHANGELOG.md`, `MIGRATION.md`, and the 4 roadmap docs.

**Deleted:**
- `src/modules/Themia.Modules.Scheduling/Migrations/20260607003329_InitialScheduling.cs`
- `src/modules/Themia.Modules.Scheduling/Migrations/20260607003329_InitialScheduling.Designer.cs`
- `src/modules/Themia.Modules.Scheduling/Migrations/SchedulingDbContextModelSnapshot.cs`
- `src/modules/Themia.Modules.Scheduling/SchedulingDbContextFactory.cs`

---

## Task 1: Framework — register the active `IDatabaseProvider` in DI

`AddThemiaDbContext` currently captures the provider in a closure but never registers it, so modules cannot
resolve the active engine. Add the registration so they can.

**Files:**
- Modify: `src/framework/Themia.Framework.Data.EFCore/Extensions/ServiceCollectionExtensions.cs`
- Test: `tests/Themia.Framework.Data.EFCore.Tests/` (the existing EFCore unit test project)

- [ ] **Step 1: Write the failing test**

Add a test (new file `AddThemiaDbContextProviderRegistrationTests.cs` in `tests/Themia.Framework.Data.EFCore.Tests/`) asserting that registering a Themia DbContext makes `IDatabaseProvider` resolvable with the right name. Use the `IDatabaseProvider` overload directly with a tiny in-test provider (or the InMemory test pattern already used in that project — match its existing style):

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.Extensions;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests;

public class AddThemiaDbContextProviderRegistrationTests
{
    private sealed class StubProvider : IDatabaseProvider
    {
        public string ProviderName => DatabaseProviderNames.Postgres;
        public void Configure(DbContextOptionsBuilder o, IConfiguration c, IServiceProvider s) => o.UseInMemoryDatabase("t");
        public void ConfigureServices(IServiceCollection s, IConfiguration c) { }
    }

    [Fact]
    public void AddThemiaDbContext_RegistersActiveDatabaseProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddThemiaDbContext<TestDbContext>(new StubProvider(), new ConfigurationBuilder().Build());

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IDatabaseProvider>();
        Assert.Equal(DatabaseProviderNames.Postgres, resolved.ProviderName);
    }
}
```

If the EFCore test project has no reusable `TestDbContext`, reuse whatever minimal `ThemiaDbContext` subclass the project already defines for its tests (check the project first); otherwise add a tiny one in the test file.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Themia.Framework.Data.EFCore.Tests/Themia.Framework.Data.EFCore.Tests.csproj --filter AddThemiaDbContext_RegistersActiveDatabaseProvider`
Expected: FAIL — `IDatabaseProvider` is not registered (`InvalidOperationException: No service for type 'IDatabaseProvider'`).

- [ ] **Step 3: Register the provider**

In `src/framework/Themia.Framework.Data.EFCore/Extensions/ServiceCollectionExtensions.cs`, in `AddThemiaDbContext<TContext>(IDatabaseProvider provider, …)`, after the `provider.ConfigureServices(services, configuration);` line, add:

```csharp
        // Make the active provider discoverable so modules can resolve the app's engine
        // (e.g. Themia.Modules.Scheduling maps it to a MigrationEngine). TryAdd: an app with
        // multiple Themia contexts shares one engine, so the first registration wins.
        services.TryAddSingleton<IDatabaseProvider>(provider);
```

Ensure `using Microsoft.Extensions.DependencyInjection.Extensions;` is present (for `TryAddSingleton`).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/Themia.Framework.Data.EFCore.Tests/Themia.Framework.Data.EFCore.Tests.csproj --filter AddThemiaDbContext_RegistersActiveDatabaseProvider`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/framework/Themia.Framework.Data.EFCore/Extensions/ServiceCollectionExtensions.cs tests/Themia.Framework.Data.EFCore.Tests/AddThemiaDbContextProviderRegistrationTests.cs
git commit -m "feat: register the active IDatabaseProvider so modules can resolve the engine"
```

---

## Task 2: Add references and author the FluentMigrator migration

**Files:**
- Modify: `src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj`
- Create: `src/modules/Themia.Modules.Scheduling/Migrations/SchedulingSchemaMigration.cs`

- [ ] **Step 1: Add the runner ProjectReference and SQL Server EF provider; remove the EF Design ref**

In `Themia.Modules.Scheduling.csproj`, in the `ProjectReference` ItemGroup add:
```xml
    <ProjectReference Include="../../neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
```
In the `PackageReference` ItemGroup add `Microsoft.EntityFrameworkCore.SqlServer` and remove the `Microsoft.EntityFrameworkCore.Design` block (no more `dotnet ef migrations`). The block becomes:
```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
    <PackageReference Include="EFCore.NamingConventions" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
```
(`FluentMigrator` for the `[Migration]` type flows transitively via `Themia.Data.Migrations`; `FluentMigrator.Runner` likewise.)

- [ ] **Step 2: Create the FM migration**

`src/modules/Themia.Modules.Scheduling/Migrations/SchedulingSchemaMigration.cs`:

```csharp
using FluentMigrator;

namespace Themia.Modules.Scheduling.Migrations;

/// <summary>
/// Creates the <c>scheduling</c> schema and its two tables (<c>execution_history</c>,
/// <c>scheduler_stats</c>). Replaces the former EF Core migration — FluentMigrator is the single
/// schema authority (DECISION #6). Run through <c>ThemiaMigrations.Run</c> at module startup.
/// </summary>
[Migration(202606120001, "Themia.Scheduling: create scheduling schema and tables")]
public sealed class SchedulingSchemaMigration : Migration
{
    private const string Schema = "scheduling";

    /// <inheritdoc />
    public override void Up()
    {
        // LOCKSTEP: this engine whitelist and the unsupported-provider guard below are two parallel
        // whitelists that MUST agree. PostgreSQL and SQL Server are the only engines with an EF provider
        // today (no EF MySQL — Pomelo has no EF Core 10 build). FluentMigrator's AsDateTimeOffset maps to
        // timestamptz (Postgres) / datetimeoffset (SQL Server), matching EF's DateTimeOffset mapping, so a
        // single CreateTable serves both. When EF MySQL lands, add a branch here AND to the guard.
        IfDatabase("postgres", "sqlserver").Delegate(CreateSchemaAndTables);

        IfDatabase(p =>
                !p.StartsWith("Postgres", System.StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", System.StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new System.NotSupportedException(
                "Themia.Scheduling supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateSchemaAndTables()
    {
        Create.Schema(Schema);

        Create.Table("execution_history").InSchema(Schema)
            .WithColumn("fire_instance_id").AsString(256).NotNullable().PrimaryKey()
            .WithColumn("scheduler_instance_id").AsString(256).Nullable()
            .WithColumn("scheduler_name").AsString(256).Nullable()
            .WithColumn("job").AsString(512).Nullable()
            .WithColumn("trigger").AsString(512).Nullable()
            .WithColumn("scheduled_fire_time_utc").AsDateTimeOffset().Nullable()
            .WithColumn("actual_fire_time_utc").AsDateTimeOffset().NotNullable()
            .WithColumn("recovering").AsBoolean().NotNullable()
            .WithColumn("vetoed").AsBoolean().NotNullable()
            .WithColumn("finished_time_utc").AsDateTimeOffset().Nullable()
            .WithColumn("exception_message").AsString(4000).Nullable();

        Create.Index("ix_execution_history_scheduler_trigger_fired")
            .OnTable("execution_history").InSchema(Schema)
            .OnColumn("scheduler_name").Ascending()
            .OnColumn("trigger").Ascending()
            .OnColumn("actual_fire_time_utc").Ascending();

        Create.Table("scheduler_stats").InSchema(Schema)
            .WithColumn("scheduler_name").AsString(256).NotNullable().PrimaryKey()
            .WithColumn("total_jobs_executed").AsInt32().NotNullable()
            .WithColumn("total_jobs_failed").AsInt32().NotNullable();
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("scheduler_stats").InSchema(Schema);
        Delete.Table("execution_history").InSchema(Schema);
        Delete.Schema(Schema);
    }
}
```

- [ ] **Step 3: Build the module to confirm it compiles**

Run: `dotnet build src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj`
Expected: PASS, 0 warnings. (The EF migration files still exist and still compile; they're removed in Task 3.)

- [ ] **Step 4: Commit**

```bash
git add src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj src/modules/Themia.Modules.Scheduling/Migrations/SchedulingSchemaMigration.cs
git commit -m "feat: add FluentMigrator schema migration for the scheduling module"
```

---

## Task 3: Provider-agnostic registration + run the FM migration in InitializeAsync

**Files:**
- Modify: `src/modules/Themia.Modules.Scheduling/SchedulingModule.cs`

- [ ] **Step 1: Update usings**

At the top of `SchedulingModule.cs`, add:
```csharp
using Themia.Data.Migrations;
using Themia.Framework.Data.EFCore.Abstractions;
```

- [ ] **Step 2: Make the DbContext registration provider-agnostic**

In `ConfigureServices`, replace the `AddDbContextFactory<SchedulingDbContext>(...)` lambda body (the part that does `dbOptions.UseNpgsql(connectionString).UseSnakeCaseNamingConvention()`) with:

```csharp
        services.AddDbContextFactory<SchedulingDbContext>((sp, dbOptions) =>
        {
            var provider = sp.GetRequiredService<IDatabaseProvider>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{ConnectionStringName}' was not found; the scheduling module requires it.");

            switch (provider.ProviderName)
            {
                case DatabaseProviderNames.Postgres:
                    dbOptions.UseNpgsql(connectionString);
                    break;
                case DatabaseProviderNames.SqlServer:
                    dbOptions.UseSqlServer(connectionString);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Themia.Scheduling supports PostgreSQL and SQL Server; provider '{provider.ProviderName}' is not supported.");
            }

            dbOptions.UseSnakeCaseNamingConvention();
        });
```

- [ ] **Step 3: Add the provider-name → MigrationEngine mapper**

Add a private static method to the class:

```csharp
    private static MigrationEngine ToMigrationEngine(string providerName) => providerName switch
    {
        DatabaseProviderNames.Postgres => MigrationEngine.Postgres,
        DatabaseProviderNames.SqlServer => MigrationEngine.SqlServer,
        _ => throw new NotSupportedException(
            $"Themia.Scheduling supports PostgreSQL and SQL Server; provider '{providerName}' is not supported."),
    };
```

- [ ] **Step 4: Run the FM migration in InitializeAsync**

Replace the whole `InitializeAsync` method with (synchronous runner; no `MigrateAsync`):

```csharp
    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        using var scope = serviceProvider.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<IDatabaseProvider>();
        var connectionString = scope.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' was not found; the scheduling module requires it.");

        ThemiaMigrations.Run(
            ToMigrationEngine(provider.ProviderName),
            connectionString,
            typeof(Migrations.SchedulingSchemaMigration).Assembly);

        return ValueTask.CompletedTask;
    }
```

- [ ] **Step 5: Update the class XML doc**

In the `<summary>`/`<remarks>` on `SchedulingModule`, change "creates/upgrades the scheduling schema on startup via EF Core migrations" to "via FluentMigrator (`ThemiaMigrations.Run`)", and replace the `<b>PostgreSQL only (this phase).</b>` paragraph (the one saying it hard-codes `UseNpgsql`) with:

```csharp
    /// <para>
    /// <b>Provider-agnostic (PostgreSQL + SQL Server).</b> The module selects the EF provider and the
    /// FluentMigrator engine from the app's registered <see cref="IDatabaseProvider"/>; it requires one
    /// (call <c>AddThemiaPostgres</c>/<c>AddThemiaSqlServer</c>). The store always uses the <c>Default</c>
    /// connection — execution history is process-wide, never tenant-routed. MySQL arrives with the EF
    /// MySQL provider.
    /// </para>
```

- [ ] **Step 6: Build**

Run: `dotnet build src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj`
Expected: PASS, 0 warnings. (`using Microsoft.EntityFrameworkCore;` is already present for `UseNpgsql`/`UseSqlServer`/`UseSnakeCaseNamingConvention`.)

- [ ] **Step 7: Commit**

```bash
git add src/modules/Themia.Modules.Scheduling/SchedulingModule.cs
git commit -m "feat: scheduling module is provider-agnostic and runs schema via shared runner"
```

---

## Task 4: Delete the EF-migration artifacts

**Files:**
- Delete: the three `Migrations/*InitialScheduling*` / snapshot files + `SchedulingDbContextFactory.cs`

- [ ] **Step 1: Delete the files**

```bash
cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia
git rm src/modules/Themia.Modules.Scheduling/Migrations/20260607003329_InitialScheduling.cs \
       src/modules/Themia.Modules.Scheduling/Migrations/20260607003329_InitialScheduling.Designer.cs \
       src/modules/Themia.Modules.Scheduling/Migrations/SchedulingDbContextModelSnapshot.cs \
       src/modules/Themia.Modules.Scheduling/SchedulingDbContextFactory.cs
```

- [ ] **Step 2: Build to confirm nothing referenced them**

Run: `dotnet build src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj`
Expected: PASS, 0 warnings. (`SchedulingDbContextFactory` was design-time only; the snapshot/designer/migration are standalone EF-tooling files. `SchedulingDbContext` and `EfExecutionHistoryStore` remain.)

- [ ] **Step 3: Commit**

```bash
git add -A src/modules/Themia.Modules.Scheduling
git commit -m "chore: remove scheduling EF migration artifacts (FluentMigrator owns the schema)"
```

---

## Task 5: Multi-engine integration tests (Postgres + SQL Server)

**Files:**
- Create: `tests/Themia.Modules.Scheduling.IntegrationTests/FakeDatabaseProvider.cs`
- Modify: `tests/Themia.Modules.Scheduling.IntegrationTests/SchedulingModuleTests.cs`
- Modify: `tests/Themia.Modules.Scheduling.IntegrationTests/Themia.Modules.Scheduling.IntegrationTests.csproj`

- [ ] **Step 1: Add the SQL Server Testcontainers package**

In the test csproj `PackageReference` ItemGroup add:
```xml
    <PackageReference Include="Testcontainers.MsSql" />
```

- [ ] **Step 2: Create the fake database provider**

`tests/Themia.Modules.Scheduling.IntegrationTests/FakeDatabaseProvider.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Abstractions;

namespace Themia.Modules.Scheduling.IntegrationTests;

/// <summary>
/// Minimal <see cref="IDatabaseProvider"/> for the scheduling tests: the module only reads
/// <see cref="ProviderName"/> (to pick the EF provider + MigrationEngine); it configures the
/// DbContext itself, so Configure/ConfigureServices are intentionally no-ops.
/// </summary>
internal sealed class FakeDatabaseProvider(string providerName) : IDatabaseProvider
{
    public string ProviderName { get; } = providerName;

    public void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        // Not used by SchedulingModule (it configures its own DbContext).
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Not used by SchedulingModule.
    }
}
```

- [ ] **Step 3: Rewrite the test file as an engine-parameterized base + two derived classes**

Replace the entire contents of `SchedulingModuleTests.cs` with:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Modules.Scheduling;
using Themia.Quartz;
using Xunit;

namespace Themia.Modules.Scheduling.IntegrationTests;

/// <summary>
/// Lifecycle integration tests for <see cref="SchedulingModule"/> against a real database:
/// <see cref="SchedulingModule.InitializeAsync"/> applies the FluentMigrator schema migration, and the
/// registered <see cref="IExecutionHistoryStore"/> resolves to the EF-backed store and round-trips data.
/// Run once per engine via the derived classes.
/// </summary>
public abstract class SchedulingModuleTestsBase
{
    protected abstract string ConnectionString { get; }
    protected abstract string ProviderName { get; }

    [Fact]
    public async Task InitializeAsync_RunsFmMigration_AndStoreRoundTrips()
    {
        var provider = BuildModuleServices();
        var module = new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" });

        await module.InitializeAsync(provider);

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();

        // The table exists (FM migration applied) — CountAsync would throw if it were absent.
        var existingCount = await context.ExecutionHistory.CountAsync();
        Assert.Equal(0, existingCount);

        var store = scope.ServiceProvider.GetRequiredService<IExecutionHistoryStore>();
        Assert.IsType<EfExecutionHistoryStore>(store);

        var entry = new ExecutionHistoryEntry
        {
            FireInstanceId = "module-fire-1",
            SchedulerName = "module-test",
            SchedulerInstanceId = "instance-1",
            Job = "job-a",
            Trigger = "trigger-a",
            ActualFireTimeUtc = DateTimeOffset.UtcNow,
            ScheduledFireTimeUtc = DateTimeOffset.UtcNow,
        };

        await store.Save(entry);
        var retrieved = await store.Get("module-fire-1");

        Assert.NotNull(retrieved);
        Assert.Equal("job-a", retrieved!.Job);
    }

    [Fact]
    public void ConfigureServices_RegistersStoreAndDashboardOptions()
    {
        var provider = BuildModuleServices();

        var store = provider.GetRequiredService<IExecutionHistoryStore>();
        Assert.IsType<EfExecutionHistoryStore>(store);

        var quartzOptions = provider.GetRequiredService<ThemiaQuartzOptions>();
        Assert.Equal("/jobs", quartzOptions.VirtualPathRoot);
        Assert.NotNull(quartzOptions.Authorize);
    }

    [Fact]
    public async Task DefaultAuthorize_AllowsAuthenticated_DeniesAnonymous()
    {
        var provider = BuildModuleServices();
        var authorize = provider.GetRequiredService<ThemiaQuartzOptions>().Authorize;
        Assert.NotNull(authorize);

        var authenticatedCtx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test")),
        };
        Assert.True(await authorize!(authenticatedCtx));

        var anonymousCtx = new DefaultHttpContext();
        Assert.False(await authorize(anonymousCtx));
    }

    private ServiceProvider BuildModuleServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<IDatabaseProvider>(new FakeDatabaseProvider(ProviderName));

        new SchedulingModule(new SchedulingModuleOptions { SchedulerName = "module-test" })
            .ConfigureServices(services);

        return services.BuildServiceProvider();
    }
}

[Trait("Category", "Integration")]
public sealed class PostgresSchedulingModuleTests : SchedulingModuleTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("themia_scheduling_module_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithCleanUp(true)
            .Build();

    protected override string ConnectionString => container.GetConnectionString();
    protected override string ProviderName => DatabaseProviderNames.Postgres;

    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}

[Trait("Category", "Integration")]
public sealed class SqlServerSchedulingModuleTests : SchedulingModuleTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    protected override string ConnectionString => container.GetConnectionString();
    protected override string ProviderName => DatabaseProviderNames.SqlServer;

    public Task InitializeAsync() => container.StartAsync();
    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
```

- [ ] **Step 4: Run the integration tests (Docker required)**

Run: `dotnet test tests/Themia.Modules.Scheduling.IntegrationTests/Themia.Modules.Scheduling.IntegrationTests.csproj`
Expected: PASS — 6 tests (3 × 2 engines). First run pulls the mssql image (several minutes). If a test fails, diagnose the real cause (e.g. an FM column-type mismatch vs EF's expectation) — do NOT weaken assertions.

- [ ] **Step 5: Commit**

```bash
git add tests/Themia.Modules.Scheduling.IntegrationTests
git commit -m "test: multi-engine (Postgres + SQL Server) scheduling module integration tests"
```

---

## Task 6: Roadmap reconciliation + release wiring

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `MIGRATION.md`, and the 4 roadmap docs.

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>0.4.6</Version>` to `<Version>0.4.7</Version>`.

- [ ] **Step 2: Reconcile the roadmap across the docs**

Update the 0.4.x progression in each of these to: `… → 0.4.6 FluentMigrator-authority foundation → 0.4.7 Scheduling EF→FM (Postgres + SQL Server) → 0.4.8 persistent Quartz (AdoJobStore + qrtz_* per-engine FM + System.Text.Json serializer) → 0.4.9 raw-connection + DbSet.Find analyzer gate; EF MySQL deferred on Pomelo's EF Core 10` —
  - `docs/themia-architecture-overview.md` (DECISION #6 spawn/roadmap line + the Quartz/`qrtz_*` note),
  - `docs/superpowers/specs/2026-06-01-themia-release-strategy-design.md` (the `0.4.x` progression line),
  - `docs/superpowers/specs/2026-06-11-themia-efcore-sqlserver-provider-design.md` (its roadmap reference),
  - `docs/superpowers/specs/2026-06-12-themia-data-migrations-runner-design.md` (its "0.4.7" scope note → now EF→FM only; persistent Quartz is 0.4.8).

Read each file's current roadmap line first and edit only that line/note (surgical; don't restructure the docs).

- [ ] **Step 3: CHANGELOG**

In `CHANGELOG.md`, below `## [Unreleased]`, insert:

```markdown

## 0.4.7 — 2026-06-12

### Changed

- **`Themia.Modules.Scheduling`** now creates its schema through FluentMigrator (the shared
  `Themia.Data.Migrations` runner, DECISION #6) instead of EF Core migrations, and is **provider-agnostic
  over PostgreSQL and SQL Server** (was PostgreSQL-only). The module selects the EF provider and migration
  engine from the app's registered `IDatabaseProvider`, so it now **requires** one
  (`AddThemiaPostgres`/`AddThemiaSqlServer`). Execution history remains process-wide (the `Default`
  connection, never tenant-routed).

### Removed

- The scheduling module's EF Core migration artifacts and design-time `DbContext` factory — its schema is
  FluentMigrator-owned.
```

- [ ] **Step 4: MIGRATION**

In `MIGRATION.md`, below the "How to read this guide" content (above `## 0.4.6`), insert:

```markdown
## 0.4.7

### Scheduling module: schema via FluentMigrator + requires an EF provider

**What changed:** `Themia.Modules.Scheduling` applies its schema with FluentMigrator at `InitializeAsync`
(through `Themia.Data.Migrations`) instead of EF Core migrations, and is now provider-agnostic over
PostgreSQL and SQL Server. It resolves the active `IDatabaseProvider` for both the EF provider and the
migration engine.

**Why:** FluentMigrator is the single schema authority (DECISION #6); the module is no longer PostgreSQL-only.

**How to upgrade:**

- Ensure an EF provider is registered before the module initializes — `AddThemiaPostgres<…>(…)` or
  `AddThemiaSqlServer<…>(…)`. Without one, the module throws at startup.
- Stop running `dotnet ef database update` for the scheduling context; the schema is applied automatically
  on startup. The table shapes are unchanged, so existing PostgreSQL databases are compatible (the FM
  migration creates the same `scheduling.execution_history` / `scheduling.scheduler_stats`).
- SQL Server is now supported.
```

- [ ] **Step 5: Full clean build**

Run: `dotnet build Themia.sln --no-incremental`
Expected: PASS, 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add Directory.Build.props CHANGELOG.md MIGRATION.md docs/
git commit -m "chore: release 0.4.7 — scheduling EF→FluentMigrator; reconcile roadmap"
```

---

## Notes for the executor

- **No `dotnet ef migrations add`** anywhere — the EF migration is replaced, not regenerated.
- **No new package versions** in `Directory.Packages.props` — `Microsoft.EntityFrameworkCore.SqlServer`
  (10.0.8) and `Testcontainers.MsSql` (4.12.0) are already pinned.
- **`trigger` is a reserved word** — FluentMigrator quotes identifiers per engine, so `WithColumn("trigger")`
  and the index column are fine; the existing EF migration already created a `trigger` column.
- The uncommitted **spec + this plan** (on `main`) should be committed on the feature branch at execution
  start, alongside the roadmap reconciliation in Task 5.
- The full-solution build is green on `main` (0.4.6 + the MessagePack fix merged); use targeted project builds
  for Tasks 1–4 and the full build for Task 6.
```
