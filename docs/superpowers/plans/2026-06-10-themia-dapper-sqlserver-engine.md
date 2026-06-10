# Themia.Framework.Data.Dapper.SqlServer Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `Themia.Framework.Data.Dapper.SqlServer` — the third and final engine for the Dapper data layer — so a Dapper-first app on SQL Server gets the same tenant-isolation / audit / soft-delete / UoW guarantees as the PostgreSQL and MySQL engines.

**Architecture:** Mirror the four-file MySQL engine package (connection factory, SQL compiler, process-global Dapper config, DI extension) against the **unchanged** engine-agnostic core. Only the two seams (`IDapperConnectionFactory`, `ISqlCompiler`) plus the `DateTimeOffset` Dapper handler are SQL-Server-specific; all reuse the shared `DapperConnectionString.Resolve(...)`. Conformance is Dapper-only (the EF data layer is PostgreSQL-only), proven by running the shared `DataLayerConformanceTests` against a real SQL Server container.

**Tech Stack:** .NET 10, `Microsoft.Data.SqlClient` 6.1.5, SqlKata 2.4.0 (`SqlServerCompiler`), Dapper, xUnit, Testcontainers.MsSql 4.12.0 (`mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04`).

**Verified facts (no core change needed):**
- SqlKata 2.4.0's `SqlServerCompiler` natively emits `;SELECT scope_identity() as Id` for `returnId` → no compiler override (same as MySQL's `LAST_INSERT_ID()`).
- `DapperUnitOfWork.ColumnValues` omits the key column when unassigned (`DapperUnitOfWork.cs:205`) → the INSERT excludes the `IDENTITY` column, so SQL Server generates it (no `SET IDENTITY_INSERT` needed).
- `DapperUnitOfWork.ConvertKey` calls `Convert.ChangeType` (`DapperUnitOfWork.cs:223`) → SCOPE_IDENTITY's `decimal` (`numeric(38,0)`) widens to the entity's `int` key.
- `Microsoft.Data.SqlClient` maps `uniqueidentifier` ↔ `Guid` natively → no Guid-format tweak (contrast MySQL).

---

## File Structure

**New package — `src/framework/Themia.Framework.Data.Dapper.SqlServer/`:**
- `SqlServerConnectionFactory.cs` — `IDapperConnectionFactory`; `new SqlConnection(DapperConnectionString.Resolve(...))`.
- `SqlServerSqlCompiler.cs` — `ISqlCompiler`; wraps `new SqlServerCompiler { UseLegacyPagination = false }`.
- `SqlServerDapperConfiguration.cs` — process-global `DateTimeOffset` Dapper handler (`DbType.DateTime2`).
- `DependencyInjection/SqlServerDapperServiceCollectionExtensions.cs` — `AddThemiaDapperSqlServer`.
- `Themia.Framework.Data.Dapper.SqlServer.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`.

**New tests — `tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/`:**
- `SqlServerContainerFixture.cs` — Testcontainers MsSql + `widgets` schema.
- `DapperSqlServerConformanceTests.cs` — `: DataLayerConformanceTests`, full suite + audit/no-tenant/µs-UTC facts.
- `SqlServerStoreGeneratedKeyTests.cs` — `INT IDENTITY(1,1)` store-gen via `scope_identity()`.
- `Themia.Framework.Data.Dapper.SqlServer.IntegrationTests.csproj`.

**Modified:** `Themia.sln` (add 2 projects), `Directory.Build.props` (`<Version>` 0.4.3→0.4.4), `CHANGELOG.md`.

---

## Task 1: Scaffold the SQL Server engine package

No Docker required — this task is a pure compile against the core. Build at the end proves the seams wire up.

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper.SqlServer/Themia.Framework.Data.Dapper.SqlServer.csproj`
- Create: `src/framework/Themia.Framework.Data.Dapper.SqlServer/SqlServerConnectionFactory.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.SqlServer/SqlServerSqlCompiler.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.SqlServer/SqlServerDapperConfiguration.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.SqlServer/DependencyInjection/SqlServerDapperServiceCollectionExtensions.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.SqlServer/PublicAPI.Shipped.txt`
- Create: `src/framework/Themia.Framework.Data.Dapper.SqlServer/PublicAPI.Unshipped.txt`
- Modify: `Themia.sln`

- [ ] **Step 1: Create the csproj**

`src/framework/Themia.Framework.Data.Dapper.SqlServer/Themia.Framework.Data.Dapper.SqlServer.csproj` — mirrors the MySQL csproj exactly, swapping the driver. `net10.0` (Framework layer). Dapper flows transitively from the core project reference (the MySQL package does not reference Dapper directly either).

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Framework.Data.Dapper.SqlServer</PackageId>
    <Description>SQL Server engine for the Themia Dapper data layer (Microsoft.Data.SqlClient + SqlKata SqlServerCompiler).</Description>
    <PackageTags>themia;dapper;sqlkata;sqlserver;mssql;data</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <PackageReference Include="SqlKata" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
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

- [ ] **Step 2: Create the connection factory**

`src/framework/Themia.Framework.Data.Dapper.SqlServer/SqlServerConnectionFactory.cs` — the simplest of the three engines: no connection-string-builder tweaks (native `uniqueidentifier`↔`Guid`).

```csharp
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Themia.Framework.Data.Dapper.Connection;

namespace Themia.Framework.Data.Dapper.SqlServer;

internal sealed class SqlServerConnectionFactory(IConfiguration configuration, IServiceProvider serviceProvider) : IDapperConnectionFactory
{
    // Microsoft.Data.SqlClient maps uniqueidentifier <-> Guid natively, so (unlike MySQL) the resolved
    // (tenant-supplied or "Default") connection string needs no Guid-format normalization.
    public DbConnection Create() => new SqlConnection(DapperConnectionString.Resolve(configuration, serviceProvider));
}
```

- [ ] **Step 3: Create the SQL compiler**

`src/framework/Themia.Framework.Data.Dapper.SqlServer/SqlServerSqlCompiler.cs` — wraps SqlKata's `SqlServerCompiler`. `UseLegacyPagination = false` selects `OFFSET … ROWS FETCH NEXT … ROWS ONLY` (SQL Server 2012+) over the legacy `ROW_NUMBER()` form; set explicitly so the choice is version-independent. No `scope_identity` rewrite — SqlKata emits it natively for `returnId`.

```csharp
using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.SqlServer;

internal sealed class SqlServerSqlCompiler : ISqlCompiler
{
    // UseLegacyPagination=false -> OFFSET/FETCH paging. Set explicitly rather than relying on the SqlKata default.
    private readonly SqlServerCompiler _compiler = new() { UseLegacyPagination = false };

    public CompiledSql Compile(Query query)
    {
        var r = _compiler.Compile(query);
        return new CompiledSql(r.Sql, r.NamedBindings);
    }
}
```

- [ ] **Step 4: Create the Dapper configuration (DateTimeOffset handler)**

`src/framework/Themia.Framework.Data.Dapper.SqlServer/SqlServerDapperConfiguration.cs` — mirrors `MySqlDapperConfiguration` but writes `DbType.DateTime2` (µs+ precision, full date range) for `datetime2(7)` columns instead of MySQL's `DbType.DateTime`.

```csharp
using System.Data;
using DapperLib = global::Dapper;

namespace Themia.Framework.Data.Dapper.SqlServer;

internal static class SqlServerDapperConfiguration
{
    private static readonly object Gate = new();
    private static volatile bool _configured;

    public static void EnsureConfigured()
    {
        if (_configured) return;
        lock (Gate)
        {
            if (_configured) return;
            // SQL Server datetime2 is tz-naive: SqlClient returns DateTime, not DateTimeOffset. Map the audit
            // DateTimeOffset properties by treating the stored value as UTC; write the UTC instant back as
            // DbType.DateTime2 (full precision/range, vs MySQL's DbType.DateTime).
            // Assumption — ONE Dapper engine per application/process: SqlMapper.AddTypeHandler is process-global,
            // registered only here by AddThemiaDapperSqlServer. The PostgreSQL engine registers no DateTimeOffset
            // handler (Npgsql surfaces DateTimeOffset natively); the MySQL engine registers a DbType.DateTime
            // variant. Loading two Dapper engines in one process is NOT supported: this handler's SQL-Server
            // SetValue would then also apply to the other engine's writes, which is incorrect.
            DapperLib.SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            _configured = true;
        }
    }

    private sealed class DateTimeOffsetTypeHandler : DapperLib.SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidCastException($"Cannot convert '{value?.GetType().Name ?? "null"}' to DateTimeOffset.")
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.DateTime2;
            parameter.Value = value.UtcDateTime;
        }
    }
}
```

- [ ] **Step 5: Create the DI extension**

`src/framework/Themia.Framework.Data.Dapper.SqlServer/DependencyInjection/SqlServerDapperServiceCollectionExtensions.cs` — the analogue of `AddThemiaDapperMySql`.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.DependencyInjection;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;

/// <summary>DI registration for the Themia Dapper data layer on SQL Server.</summary>
public static class SqlServerDapperServiceCollectionExtensions
{
    /// <summary>Registers the Themia Dapper data layer on SQL Server. The connection string is resolved per
    /// scope from <c>ITenantAccessor.Current?.ConnectionString</c>, falling back to the "Default" connection
    /// string. Audit timestamps round-trip via a <c>datetime2</c> <see cref="System.DateTimeOffset"/> handler.</summary>
    public static IServiceCollection AddThemiaDapperSqlServer(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DapperDataOptions>? configure = null)
    {
        SqlServerDapperConfiguration.EnsureConfigured();
        services.AddThemiaDapperCore(configure);
        services.AddScoped<IDapperConnectionFactory>(sp => new SqlServerConnectionFactory(configuration, sp));
        services.AddSingleton<ISqlCompiler, SqlServerSqlCompiler>();
        return services;
    }
}
```

- [ ] **Step 6: Create the PublicAPI files**

`src/framework/Themia.Framework.Data.Dapper.SqlServer/PublicAPI.Shipped.txt` — empty marker:

```text
#nullable enable
```

`src/framework/Themia.Framework.Data.Dapper.SqlServer/PublicAPI.Unshipped.txt`:

```text
#nullable enable
Themia.Framework.Data.Dapper.SqlServer.DependencyInjection.SqlServerDapperServiceCollectionExtensions
static Themia.Framework.Data.Dapper.SqlServer.DependencyInjection.SqlServerDapperServiceCollectionExtensions.AddThemiaDapperSqlServer(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Microsoft.Extensions.Configuration.IConfiguration! configuration, System.Action<Themia.Framework.Data.Dapper.DapperDataOptions!>? configure = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

- [ ] **Step 7: Add the project to the solution**

Run from `Packages/themia/`:

```bash
dotnet sln Themia.sln add src/framework/Themia.Framework.Data.Dapper.SqlServer/Themia.Framework.Data.Dapper.SqlServer.csproj
```

- [ ] **Step 8: Build the package (clean, to surface RS0016 PublicAPI diagnostics)**

Run: `dotnet build src/framework/Themia.Framework.Data.Dapper.SqlServer/Themia.Framework.Data.Dapper.SqlServer.csproj --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors. (Any `RS0016` means a public member is missing from `PublicAPI.Unshipped.txt`.)

- [ ] **Step 9: Commit**

```bash
git add src/framework/Themia.Framework.Data.Dapper.SqlServer Themia.sln
git commit -m "feat: scaffold Themia.Framework.Data.Dapper.SqlServer engine"
```

---

## Task 2: SQL Server conformance suite

Requires Docker (Testcontainers pulls `mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04`, ~1.5 GB; first run is slow). Proves the engine honours the full shared contract.

**Files:**
- Create: `tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests.csproj`
- Create: `tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/SqlServerContainerFixture.cs`
- Create: `tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/DapperSqlServerConformanceTests.cs`
- Modify: `Themia.sln`

- [ ] **Step 1: Create the integration-test csproj**

`tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests.csproj` — mirrors the MySQL integration csproj, swapping the driver/Testcontainers package.

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
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <PackageReference Include="Microsoft.Extensions.Configuration" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Data.Dapper.Conformance/Themia.Framework.Data.Dapper.Conformance.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper.SqlServer/Themia.Framework.Data.Dapper.SqlServer.csproj" />
    <ProjectReference Include="../../src/framework/Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the container fixture**

`tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/SqlServerContainerFixture.cs`. SQL Server has no `MSSQL_DATABASE` env, so use the default `master` database (don't call `WithDatabase`); the builder sets a strong SA password automatically. SQL Server does not support `CREATE TABLE IF NOT EXISTS`, so guard with `OBJECT_ID`. Image tag pinned (the package's tested default).

```csharp
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Xunit;

namespace Themia.Framework.Data.Dapper.SqlServer.IntegrationTests;

/// <summary>
/// Spins up a real SQL Server container and creates the shared <c>widgets</c> table the Dapper provider maps to.
/// Tables live in the default <c>master</c> database (the mssql image creates no custom database).
/// <see cref="ResetAsync"/> truncates between facts.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
        .WithCleanUp(true)
        .Build();

    /// <summary>The connection string to the running container (master database).</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        await CreateSchemaAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await container.DisposeAsync();

    /// <summary>Clears the shared table so each fact starts from an empty state.</summary>
    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE widgets";
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateSchemaAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'widgets', N'U') IS NULL
            CREATE TABLE widgets (
                id                UNIQUEIDENTIFIER  NOT NULL PRIMARY KEY,
                tenant_id         NVARCHAR(100)     NULL,
                name              NVARCHAR(200)     NOT NULL,
                quantity          INT               NOT NULL,
                created_at        DATETIME2(7)      NOT NULL,
                created_by        NVARCHAR(100)     NULL,
                last_modified_at  DATETIME2(7)      NULL,
                last_modified_by  NVARCHAR(100)     NULL,
                is_deleted        BIT               NOT NULL DEFAULT 0,
                deleted_at        DATETIME2(7)      NULL,
                deleted_by        NVARCHAR(100)     NULL,
                restored_at       DATETIME2(7)      NULL,
                restored_by       NVARCHAR(100)     NULL
            )
            """;
        await command.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 3: Create the conformance subclass (full shared suite + the three extra facts)**

`tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/DapperSqlServerConformanceTests.cs` — the SQL Server analogue of `DapperMySqlConformanceTests`. Same three added facts (audit-user stamping, no-tenant soft-delete parity, µs-precision UTC round-trip), swapping the DI call to `AddThemiaDapperSqlServer`.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.SqlServer.IntegrationTests;

/// <summary>Runs the shared data-layer contract against the Dapper-on-SQL-Server provider.</summary>
[Trait("Category", "Integration")]
public sealed class DapperSqlServerConformanceTests(SqlServerContainerFixture fixture)
    : DataLayerConformanceTests, IClassFixture<SqlServerContainerFixture>
{
    /// <inheritdoc />
    protected override Task<ConformanceScope> NewScopeAsync(TenantId? tenant)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        services.AddThemiaDapperSqlServer(configuration);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var filter = scope.ServiceProvider.GetRequiredService<IDataFilterScope>();

        return Task.FromResult(new ConformanceScope(provider, scope, repo, uow, filter));
    }

    /// <inheritdoc />
    protected override Task ResetAsync() => fixture.ResetAsync();

    /// <summary>
    /// Audit user columns (CreatedBy on insert, LastModifiedBy on update) are stamped from the ambient
    /// <see cref="ICurrentUserAccessor"/> by the Dapper unit of work.
    /// </summary>
    [Fact]
    public async Task AuditUser_IsStamped_OnInsertAndUpdate()
    {
        await ResetAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaDapperSqlServer(configuration);
        services.AddSingleton<ICurrentUserAccessor>(new StubCurrentUser("user-42"));   // override the null default
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var widget = new Widget { Name = "audited", Quantity = 1 };
        widget.SetId(Guid.NewGuid());
        await repo.AddAsync(widget);
        await uow.SaveChangesAsync();
        Assert.Equal("user-42", (await repo.GetByIdAsync(widget.Id))!.CreatedBy);

        widget.Quantity = 2;
        repo.Update(widget);
        await uow.SaveChangesAsync();
        Assert.Equal("user-42", (await repo.GetByIdAsync(widget.Id))!.LastModifiedBy);
    }

    /// <summary>
    /// A no-tenant (system) scope cannot soft-delete a tenant-owned row: Dapper scopes the soft-delete to
    /// global (tenant_id IS NULL) rows when no tenant is ambient, so the cross-tenant delete matches 0 rows
    /// and throws <see cref="ConcurrencyException"/>. Parity with the PostgreSQL/MySQL integration projects.
    /// </summary>
    [Fact]
    public async Task NoTenantScope_CannotSoftDelete_TenantOwnedRow()
    {
        await ResetAsync();

        Guid id;
        await using (var a = await NewScopeAsync(new TenantId("a")))
        {
            var w = new Widget { Name = "owned", Quantity = 1 };
            w.SetId(Guid.NewGuid());
            id = w.Id;
            await a.Repo.AddAsync(w);
            await a.Uow.SaveChangesAsync();
        }

        await using (var system = await NewScopeAsync(null))
        {
            var detached = new Widget { Name = "owned", Quantity = 1 };
            detached.SetId(id);
            system.Repo.Remove(detached);
            // WHERE id = @id AND tenant_id IS NULL matches 0 rows, so the cross-tenant delete fails loud.
            await Assert.ThrowsAsync<ConcurrencyException>(() => system.Uow.SaveChangesAsync());
        }

        await using var check = await NewScopeAsync(new TenantId("a"));
        Assert.NotNull(await check.Repo.GetByIdAsync(id));   // the tenant row survived the cross-tenant delete attempt
    }

    /// <summary>
    /// The <c>DateTimeOffset</c> ⇄ <c>datetime2(7)</c> handler round-trips a known UTC instant with microsecond
    /// precision and re-labels it UTC — guarding against offset corruption (e.g. on a non-UTC agent) and
    /// sub-second precision loss that the existence-only audit facts would not catch.
    /// </summary>
    [Fact]
    public async Task AuditTimestamp_RoundTripsUtc_WithMicrosecondPrecision()
    {
        await ResetAsync();

        // A fixed UTC instant whose fractional part is exactly microsecond-aligned (6_543_210 ticks = 654_321 µs).
        var stamped = new DateTimeOffset(2026, 6, 10, 9, 8, 7, TimeSpan.Zero).AddTicks(6_543_210);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(stamped));   // stamps audit fields deterministically
        services.AddThemiaDapperSqlServer(configuration);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var widget = new Widget { Name = "ts", Quantity = 1 };
        widget.SetId(Guid.NewGuid());
        await repo.AddAsync(widget);
        await uow.SaveChangesAsync();

        var loaded = await repo.GetByIdAsync(widget.Id);
        Assert.NotNull(loaded);
        Assert.Equal(TimeSpan.Zero, loaded!.CreatedAt.Offset);   // the handler re-labels the stored value UTC
        var deltaTicks = Math.Abs((loaded.CreatedAt.UtcDateTime - stamped.UtcDateTime).Ticks);
        Assert.True(deltaTicks <= TimeSpan.TicksPerMicrosecond,
            $"CreatedAt round-trip lost precision: expected ~{stamped.UtcDateTime:O}, got {loaded.CreatedAt.UtcDateTime:O}");
    }

    private sealed class StubCurrentUser(string? userId) : ICurrentUserAccessor
    {
        public string? UserId { get; } = userId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
```

- [ ] **Step 4: Add the test project to the solution**

Run from `Packages/themia/`:

```bash
dotnet sln Themia.sln add tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests.csproj
```

- [ ] **Step 5: Run the conformance suite (Docker required)**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests.csproj`
Expected: PASS. The full shared `DataLayerConformanceTests` suite plus the three added facts all green. (First run pulls the ~1.5 GB image — allow several minutes.)

- [ ] **Step 6: Commit**

```bash
git add tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests Themia.sln
git commit -m "test: SQL Server Dapper conformance suite (Testcontainers)"
```

---

## Task 3: Store-generated IDENTITY-int key test

Requires Docker (reuses the same container fixture). Locks the one SQL-Server-specific write path: `INT IDENTITY(1,1)` populated back via native `scope_identity()` → `decimal` → `int`.

**Files:**
- Create: `tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/SqlServerStoreGeneratedKeyTests.cs`

- [ ] **Step 1: Write the store-gen test**

`tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/SqlServerStoreGeneratedKeyTests.cs` — the SQL Server analogue of `MySqlStoreGeneratedKeyTests`. The `gadgets` table uses `INT IDENTITY(1,1)`; the unassigned (0) key is omitted from the INSERT (`ColumnValues` drops it), SQL Server generates it, and the UoW reads it back via `scope_identity()`.

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.SqlServer.IntegrationTests;

/// <summary>
/// Entity with a store-generated IDENTITY integer key. No assignment — the key is left at 0 so SQL Server
/// generates it and the UoW reads it back via scope_identity().
/// </summary>
public class Gadget : AuditableEntity<int>, ITenantEntity
{
    /// <summary>The owning tenant, stamped by the data layer on insert.</summary>
    public TenantId? TenantId { get; set; }

    /// <summary>The gadget name.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// Verifies the SQL Server store-generated-key path: an IDENTITY int key is populated back onto the entity
/// after save via SqlKata's native scope_identity() (store-generated Guid is PostgreSQL-only — no SQL Server
/// RETURNING). SCOPE_IDENTITY returns numeric(38,0) -> decimal, which ConvertKey widens to int. The
/// <c>gadgets</c> table intentionally omits soft-delete columns because <see cref="Gadget"/> extends
/// <see cref="AuditableEntity{TKey}"/>, not <c>SoftDeletableEntity</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlServerStoreGeneratedKeyTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    [Fact]
    public async Task Add_WithIdentityKey_PopulatesKeyAfterSave()
    {
        await using (var setup = new SqlConnection(fixture.ConnectionString))
        {
            await setup.OpenAsync();
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = """
                IF OBJECT_ID(N'gadgets', N'U') IS NULL
                CREATE TABLE gadgets (
                    id               INT            NOT NULL IDENTITY(1,1) PRIMARY KEY,
                    tenant_id        NVARCHAR(100)  NULL,
                    name             NVARCHAR(200)  NOT NULL,
                    created_at       DATETIME2(7)   NOT NULL,
                    created_by       NVARCHAR(100)  NULL,
                    last_modified_at DATETIME2(7)   NULL,
                    last_modified_by NVARCHAR(100)  NULL
                )
                """;
            await cmd.ExecuteNonQueryAsync();
            // DELETE (not TRUNCATE) keeps the test independent of prior IDENTITY state; the assertion checks != 0.
            cmd.CommandText = "DELETE FROM gadgets";
            await cmd.ExecuteNonQueryAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaDapperSqlServer(configuration);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Gadget, int>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var gadget = new Gadget { Name = "gadget-zero" };
        Assert.Equal(0, gadget.Id);

        await repo.AddAsync(gadget);
        await uow.SaveChangesAsync();

        Assert.NotEqual(0, gadget.Id);   // populated from scope_identity()

        var fetched = await repo.GetByIdAsync(gadget.Id);
        Assert.NotNull(fetched);
        Assert.Equal("gadget-zero", fetched!.Name);
    }
}
```

- [ ] **Step 2: Run the store-gen test (Docker required)**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests.csproj --filter "FullyQualifiedName~SqlServerStoreGeneratedKeyTests"`
Expected: PASS — `gadget.Id` populated non-zero from `scope_identity()`, row round-trips.

- [ ] **Step 3: Commit**

```bash
git add tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/SqlServerStoreGeneratedKeyTests.cs
git commit -m "test: SQL Server store-generated IDENTITY key via scope_identity()"
```

---

## Task 4: Finalize — version bump, CHANGELOG, full build

No Docker required.

**Files:**
- Modify: `Directory.Build.props` (line 26)
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Bump the version to 0.4.4**

In `Directory.Build.props`, change `<Version>0.4.3</Version>` to `<Version>0.4.4</Version>`.

- [ ] **Step 2: Add the CHANGELOG entry**

Add a new section at the top of `CHANGELOG.md` (under the header, above the `## [0.4.3]` entry), matching the existing format:

```markdown
## [0.4.4]

### Added
- `Themia.Framework.Data.Dapper.SqlServer` — SQL Server engine for the Dapper data layer
  (`Microsoft.Data.SqlClient` + SqlKata `SqlServerCompiler`). Completes the three-engine set
  (PostgreSQL, MySQL, SQL Server). Native `uniqueidentifier`↔`Guid` mapping, `OFFSET/FETCH` paging
  (`UseLegacyPagination = false`), `datetime2(7)` audit timestamps via a `DbType.DateTime2`
  `DateTimeOffset` handler, and store-generated `INT IDENTITY(1,1)` keys via native `scope_identity()`.
  Conformance is Dapper-only (the EF data layer remains PostgreSQL-only), proven against a real SQL Server
  container.
```

- [ ] **Step 3: Full clean build + all non-Docker tests**

Run: `dotnet build Themia.sln --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors (PublicAPI documented, all TFMs).

Run: `dotnet test Themia.sln --filter "Category!=Integration"`
Expected: PASS — the existing unit suites still green (no regression; the new engine's only tests are Integration-tagged).

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props CHANGELOG.md
git commit -m "chore: bump to 0.4.4 (SQL Server Dapper engine)"
```

---

## Self-Review notes

- **Spec coverage:** factory (T1.S2), compiler + `UseLegacyPagination=false` (T1.S3), `datetime2`/`DbType.DateTime2` handler (T1.S4), DI (T1.S5), PublicAPI/csproj/sln (T1.S1/6/7), Dapper-only conformance (T2), `INT IDENTITY` store-gen via `scope_identity()` (T3), version/CHANGELOG (T4). All spec sections map to a task.
- **No core changes** — every SQL-Server path verified against the existing core (`ColumnValues:205`, `ConvertKey:223`) and the SqlKata 2.4.0 assembly (`scope_identity()`); the plan adds only the new package + tests + version/CHANGELOG.
- **Type consistency:** `AddThemiaDapperSqlServer`, `SqlServerConnectionFactory`, `SqlServerSqlCompiler`, `SqlServerDapperConfiguration`, `SqlServerContainerFixture`, `Gadget : AuditableEntity<int>, ITenantEntity` — names used consistently across tasks and PublicAPI.
- **Known divergences from MySQL (intentional):** `DbType.DateTime2` (not `DateTime`); no Guid-format tweak; `OBJECT_ID` guard instead of `IF NOT EXISTS`; `DELETE` instead of `TRUNCATE` in the store-gen test (IDENTITY independence); `master` database (no `WithDatabase`).
