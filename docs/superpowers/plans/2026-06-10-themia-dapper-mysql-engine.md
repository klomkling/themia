# Themia.Framework.Data.Dapper.MySql Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a MySQL engine package for the Dapper data layer — the sibling to `Themia.Framework.Data.Dapper.PostgreSql` — so a Dapper-first app on MySQL gets the framework's tenant isolation, audit, soft-delete, and UoW guarantees.

**Architecture:** Mirror the three-file PostgreSQL engine (connection factory + SqlKata compiler + DI extension) against the unchanged engine-agnostic `Themia.Framework.Data.Dapper` core. MySQL specifics: `MySqlConnector` driver, `GuidFormat=Char36` (else phantom-empty Guids), a `DateTimeOffset` Dapper type handler (MySqlConnector returns `DateTime` for `DATETIME`), and `LAST_INSERT_ID()` store-gen keys (SqlKata-native). Conformance is Dapper-only (the EF data layer is PostgreSQL-only).

**Tech Stack:** .NET 10, Dapper, SqlKata (`MySqlCompiler`), MySqlConnector 2.6.0, xUnit, Testcontainers.MySql 4.12.0 (`mysql:8.4`). Branch: `feat/dapper-mysql-engine`. Spec: `docs/superpowers/specs/2026-06-10-themia-dapper-mysql-engine-design.md`.

---

## File Structure

**New engine package** `src/framework/Themia.Framework.Data.Dapper.MySql/`:
- `Themia.Framework.Data.Dapper.MySql.csproj` — net10 package, refs Dapper core + MySqlConnector + SqlKata.
- `MySqlConnectionFactory.cs` — `IDapperConnectionFactory`; tenant-CS resolution; forces `GuidFormat=Char36`.
- `MySqlSqlCompiler.cs` — `ISqlCompiler` wrapping SqlKata `MySqlCompiler`.
- `MySqlDapperConfiguration.cs` — registers a Dapper `DateTimeOffset` type handler (MySQL temporal adaptation).
- `DependencyInjection/MySqlDapperServiceCollectionExtensions.cs` — `AddThemiaDapperMySql`.
- `PublicAPI.Shipped.txt` (empty) + `PublicAPI.Unshipped.txt` (the one public method).

**New test project** `tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/`:
- `Themia.Framework.Data.Dapper.MySql.IntegrationTests.csproj`.
- `MySqlContainerFixture.cs` — `mysql:8.4` Testcontainers + `widgets` schema + reset.
- `DapperMySqlConformanceTests.cs` — `: DataLayerConformanceTests`, Dapper-MySQL scope.
- `MySqlStoreGeneratedKeyTests.cs` — `Gadget : AuditableEntity<int>` + `gadgets` AUTO_INCREMENT + `LAST_INSERT_ID` test.

**Modified:** `Themia.sln` (add both projects); `CHANGELOG.md` (`[Unreleased]` Added note).

The shared `DataLayerConformanceTests` is the test driver — Task 2 wires it to MySQL and is the integration proof for the engine code from Task 1.

---

### Task 1: Scaffold the MySQL engine package

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper.MySql/Themia.Framework.Data.Dapper.MySql.csproj`
- Create: `src/framework/Themia.Framework.Data.Dapper.MySql/MySqlConnectionFactory.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.MySql/MySqlSqlCompiler.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.MySql/MySqlDapperConfiguration.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.MySql/DependencyInjection/MySqlDapperServiceCollectionExtensions.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.MySql/PublicAPI.Shipped.txt`
- Create: `src/framework/Themia.Framework.Data.Dapper.MySql/PublicAPI.Unshipped.txt`
- Modify: `Themia.sln`

- [ ] **Step 1: Create the csproj**

`src/framework/Themia.Framework.Data.Dapper.MySql/Themia.Framework.Data.Dapper.MySql.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Framework.Data.Dapper.MySql</PackageId>
    <Description>MySQL engine for the Themia Dapper data layer (MySqlConnector + SqlKata MySqlCompiler).</Description>
    <PackageTags>themia;dapper;sqlkata;mysql;mysqlconnector;data</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="MySqlConnector" />
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

- [ ] **Step 2: Create `MySqlConnectionFactory.cs`**

Mirrors `NpgsqlConnectionFactory` but forces `GuidFormat=Char36`. The tenant connection string comes from `ITenantAccessor.Current?.ConnectionString` (resolved via the service provider to avoid a hard MultiTenancy dependency in the type signature), falling back to the `"Default"` connection string.
```csharp
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Themia.Framework.Data.Dapper.Connection;
using Themia.MultiTenancy.Abstractions;

namespace Themia.Framework.Data.Dapper.MySql;

internal sealed class MySqlConnectionFactory(IConfiguration configuration, IServiceProvider serviceProvider) : IDapperConnectionFactory
{
    private const string DefaultConnectionName = "Default";

    public DbConnection Create()
    {
        // Themia entities use Guid keys stored as CHAR(36); MySqlConnector's default Guid format would
        // otherwise yield phantom-empty Guids. Force Char36 idempotently.
        var builder = new MySqlConnectionStringBuilder(Resolve()) { GuidFormat = MySqlGuidFormat.Char36 };
        return new MySqlConnection(builder.ConnectionString);
    }

    private string Resolve()
    {
        var tenantCs = (serviceProvider.GetService(typeof(ITenantAccessor)) as ITenantAccessor)?.Current?.ConnectionString;
        if (!string.IsNullOrWhiteSpace(tenantCs)) return tenantCs;

        var cs = configuration.GetConnectionString(DefaultConnectionName);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"No tenant connection string was resolved and connection string '{DefaultConnectionName}' was not found or is empty.");

        return cs;
    }
}
```

- [ ] **Step 3: Create `MySqlSqlCompiler.cs`**

Wraps SqlKata's `MySqlCompiler`. No `LAST_INSERT_ID` rewrite — SqlKata emits it natively for `returnId`.
```csharp
using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.MySql;

internal sealed class MySqlSqlCompiler : ISqlCompiler
{
    private readonly MySqlCompiler _compiler = new();

    public CompiledSql Compile(Query query)
    {
        var r = _compiler.Compile(query);
        return new CompiledSql(r.Sql, r.NamedBindings);
    }
}
```

- [ ] **Step 4: Create `MySqlDapperConfiguration.cs`**

MySqlConnector returns `DateTime` (not `DateTimeOffset`) for `DATETIME` columns; the audit fields are `DateTimeOffset`, so register a Dapper handler that reads stored `DATETIME` as UTC and writes UTC. Uses the `global::Dapper` alias to avoid the namespace collision with `Themia.Framework.Data.Dapper` (same pattern as the core's `DapperConfiguration`).
```csharp
using System.Data;
using DapperLib = global::Dapper;

namespace Themia.Framework.Data.Dapper.MySql;

internal static class MySqlDapperConfiguration
{
    private static readonly object Gate = new();
    private static volatile bool _configured;

    public static void EnsureConfigured()
    {
        if (_configured) return;
        lock (Gate)
        {
            if (_configured) return;
            // MySQL DATETIME is tz-naive: MySqlConnector returns DateTime, not DateTimeOffset. Map the audit
            // DateTimeOffset properties by treating the stored value as UTC; write the UTC instant back.
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

`DependencyInjection/MySqlDapperServiceCollectionExtensions.cs`:
```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.DependencyInjection;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.MySql.DependencyInjection;

/// <summary>DI registration for the Themia Dapper data layer on MySQL.</summary>
public static class MySqlDapperServiceCollectionExtensions
{
    /// <summary>Registers the Themia Dapper data layer on MySQL. The connection string is resolved per scope
    /// from <c>ITenantAccessor.Current?.ConnectionString</c>, falling back to the "Default" connection string.
    /// <c>GuidFormat=Char36</c> is enforced for Guid keys.</summary>
    public static IServiceCollection AddThemiaDapperMySql(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DapperDataOptions>? configure = null)
    {
        MySqlDapperConfiguration.EnsureConfigured();
        services.AddThemiaDapperCore(configure);
        services.AddScoped<IDapperConnectionFactory>(sp => new MySqlConnectionFactory(configuration, sp));
        services.AddSingleton<ISqlCompiler, MySqlSqlCompiler>();
        return services;
    }
}
```

- [ ] **Step 6: Create the PublicAPI files**

`PublicAPI.Shipped.txt` — empty file (create it with no content).

`PublicAPI.Unshipped.txt`:
```text
#nullable enable
Themia.Framework.Data.Dapper.MySql.DependencyInjection.MySqlDapperServiceCollectionExtensions
static Themia.Framework.Data.Dapper.MySql.DependencyInjection.MySqlDapperServiceCollectionExtensions.AddThemiaDapperMySql(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Microsoft.Extensions.Configuration.IConfiguration! configuration, System.Action<Themia.Framework.Data.Dapper.DapperDataOptions!>? configure = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

- [ ] **Step 7: Add the project to the solution**

Run: `dotnet sln Themia.sln add src/framework/Themia.Framework.Data.Dapper.MySql/Themia.Framework.Data.Dapper.MySql.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 8: Build the package (verify it compiles clean)**

Run: `dotnet build src/framework/Themia.Framework.Data.Dapper.MySql/Themia.Framework.Data.Dapper.MySql.csproj --no-incremental`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (PublicAPI analyzers see `AddThemiaDapperMySql` documented in `PublicAPI.Unshipped.txt`).

- [ ] **Step 9: Commit**

```bash
git add src/framework/Themia.Framework.Data.Dapper.MySql Themia.sln
git commit -m "feat(data-dapper-mysql): scaffold the MySQL engine package

MySqlConnectionFactory (GuidFormat=Char36), MySqlSqlCompiler (SqlKata
MySqlCompiler), a DateTimeOffset Dapper handler for tz-naive DATETIME, and
AddThemiaDapperMySql DI — mirrors the PostgreSql engine. Validated by the
conformance suite next."
```

---

### Task 2: MySQL conformance (the integration proof)

**Files:**
- Create: `tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/Themia.Framework.Data.Dapper.MySql.IntegrationTests.csproj`
- Create: `tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/MySqlContainerFixture.cs`
- Create: `tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/DapperMySqlConformanceTests.cs`
- Modify: `Themia.sln`

Requires Docker (Testcontainers).

- [ ] **Step 1: Create the integration test csproj**

`tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/Themia.Framework.Data.Dapper.MySql.IntegrationTests.csproj`:
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
    <PackageReference Include="Testcontainers.MySql" />
    <PackageReference Include="MySqlConnector" />
    <PackageReference Include="Microsoft.Extensions.Configuration" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Data.Dapper.Conformance/Themia.Framework.Data.Dapper.Conformance.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper.MySql/Themia.Framework.Data.Dapper.MySql.csproj" />
    <ProjectReference Include="../../src/framework/Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the MySQL container fixture**

`tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/MySqlContainerFixture.cs`. The `widgets` schema mirrors the PostgreSQL one with MySQL types (`CHAR(36)` Guid PK, `DATETIME(6)` timestamps, `TINYINT(1)` bool):
```csharp
using MySqlConnector;
using Testcontainers.MySql;
using Xunit;

namespace Themia.Framework.Data.Dapper.MySql.IntegrationTests;

/// <summary>
/// Spins up a real MySQL container and creates the shared <c>widgets</c> table the Dapper provider maps to.
/// <see cref="ResetAsync"/> truncates between facts.
/// </summary>
public sealed class MySqlContainerFixture : IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4")
        .WithDatabase("themia_dapper_tests")
        .WithUsername("themia")
        .WithPassword("themia")
        .WithCleanUp(true)
        .Build();

    /// <summary>The connection string to the running container.</summary>
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
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE widgets";
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateSchemaAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS widgets (
                id                CHAR(36)      NOT NULL PRIMARY KEY,
                tenant_id         VARCHAR(100)  NULL,
                name              VARCHAR(200)  NOT NULL,
                quantity          INT           NOT NULL,
                created_at        DATETIME(6)   NOT NULL,
                created_by        VARCHAR(100)  NULL,
                last_modified_at  DATETIME(6)   NULL,
                last_modified_by  VARCHAR(100)  NULL,
                is_deleted        TINYINT(1)    NOT NULL DEFAULT 0,
                deleted_at        DATETIME(6)   NULL,
                deleted_by        VARCHAR(100)  NULL,
                restored_at       DATETIME(6)   NULL,
                restored_by       VARCHAR(100)  NULL
            )
            """;
        await command.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 3: Create the conformance subclass**

`tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/DapperMySqlConformanceTests.cs` — mirrors `DapperPostgresConformanceTests` but wires `AddThemiaDapperMySql` and disposes the root provider via `ConformanceScope`:
```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.Dapper.MySql.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.MySql.IntegrationTests;

/// <summary>Runs the shared data-layer contract against the Dapper-on-MySQL provider.</summary>
[Trait("Category", "Integration")]
public sealed class DapperMySqlConformanceTests(MySqlContainerFixture fixture)
    : DataLayerConformanceTests, IClassFixture<MySqlContainerFixture>
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
        services.AddThemiaDapperMySql(configuration);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var filter = scope.ServiceProvider.GetRequiredService<IDataFilterScope>();

        return Task.FromResult(new ConformanceScope(provider, scope, repo, uow, filter));
    }

    /// <inheritdoc />
    protected override Task ResetAsync() => fixture.ResetAsync();
}
```

- [ ] **Step 4: Add the test project to the solution**

Run: `dotnet sln Themia.sln add tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/Themia.Framework.Data.Dapper.MySql.IntegrationTests.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 5: Run the conformance suite against MySQL**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/Themia.Framework.Data.Dapper.MySql.IntegrationTests.csproj`
Expected: PASS — all shared `DataLayerConformanceTests` facts green against Dapper-MySQL (CRUD round-trip + audit stamping, tenant A≠B, cross-tenant write throws / under-bypass succeeds, no-tenant→tenant throws, soft-delete hide/restore, paging+total, IN-list, transaction rollback). Requires Docker.

If a failure occurs, it is almost certainly engine glue (Task 1) — debug the factory/compiler/handler, not the shared facts. Common MySQL gotchas already handled: Guid via `Char36`+`CHAR(36)`; `DateTimeOffset` via the type handler; `bool`↔`TINYINT(1)` is native to MySqlConnector. Do NOT weaken any shared fact.

- [ ] **Step 6: Commit**

```bash
git add tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests Themia.sln
git commit -m "test(data-dapper-mysql): run the shared conformance suite on Dapper-MySQL

MySqlContainerFixture (mysql:8.4) + DapperMySqlConformanceTests proves the MySQL
engine honours the full data-layer contract (tenant isolation read+write, audit,
soft-delete, UoW/transactions, paging). Dapper-only (no EF-MySQL provider)."
```

---

### Task 3: MySQL store-generated-key test (AUTO_INCREMENT)

**Files:**
- Create: `tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/MySqlStoreGeneratedKeyTests.cs`

- [ ] **Step 1: Write the store-gen test with an AUTO_INCREMENT int entity**

`tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/MySqlStoreGeneratedKeyTests.cs`. The `Gadget` has an `int` key left at 0 so the DB generates it; the UoW's store-gen path (`returnId: true` → `LAST_INSERT_ID()`) must populate it back.
```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.MySql.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.MySql.IntegrationTests;

/// <summary>
/// Entity with a store-generated AUTO_INCREMENT integer key. No assignment — the key is left at 0 so MySQL
/// generates it and the UoW reads it back via LAST_INSERT_ID().
/// </summary>
public class Gadget : AuditableEntity<int>, ITenantEntity
{
    /// <summary>The owning tenant, stamped by the data layer on insert.</summary>
    public TenantId? TenantId { get; set; }

    /// <summary>The gadget name.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// Verifies the MySQL store-generated-key path: an AUTO_INCREMENT int key is populated back onto the entity
/// after save via SqlKata's native LAST_INSERT_ID() (store-generated UUID is PostgreSQL-only — no MySQL RETURNING).
/// </summary>
[Trait("Category", "Integration")]
public sealed class MySqlStoreGeneratedKeyTests(MySqlContainerFixture fixture) : IClassFixture<MySqlContainerFixture>
{
    [Fact]
    public async Task Add_WithAutoIncrementKey_PopulatesKeyAfterSave()
    {
        await using (var setup = new MySqlConnection(fixture.ConnectionString))
        {
            await setup.OpenAsync();
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS gadgets (
                    id               BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    tenant_id        VARCHAR(100)  NULL,
                    name             VARCHAR(200)  NOT NULL,
                    created_at       DATETIME(6)   NOT NULL,
                    created_by       VARCHAR(100)  NULL,
                    last_modified_at DATETIME(6)   NULL,
                    last_modified_by VARCHAR(100)  NULL
                )
                """;
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "TRUNCATE TABLE gadgets";
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
        services.AddThemiaDapperMySql(configuration);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Gadget, int>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var gadget = new Gadget { Name = "gadget-zero" };
        Assert.Equal(0, gadget.Id);

        await repo.AddAsync(gadget);
        await uow.SaveChangesAsync();

        Assert.NotEqual(0, gadget.Id);   // populated from LAST_INSERT_ID()

        var fetched = await repo.GetByIdAsync(gadget.Id);
        Assert.NotNull(fetched);
        Assert.Equal("gadget-zero", fetched!.Name);
    }
}
```

- [ ] **Step 2: Run the store-gen test**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/Themia.Framework.Data.Dapper.MySql.IntegrationTests.csproj --filter Add_WithAutoIncrementKey_PopulatesKeyAfterSave`
Expected: PASS.

If it fails because SqlKata's `;SELECT LAST_INSERT_ID()` multi-statement does not round-trip through `MySqlConnection.ExecuteScalarAsync`, the minimal fix is a `MySqlSqlCompiler` override that strips SqlKata's trailing `LAST_INSERT_ID()` select and the UoW reads `MySqlCommand.LastInsertedId` — but verify the SqlKata-native path first; MySqlConnector enables multi-statements by default, so it is expected to work. Report back with the exact SQL/error if it does not (this is the spec's one flagged implementation unknown).

- [ ] **Step 3: Commit**

```bash
git add tests/Themia.Framework.Data.Dapper.MySql.IntegrationTests/MySqlStoreGeneratedKeyTests.cs
git commit -m "test(data-dapper-mysql): store-generated AUTO_INCREMENT key via LAST_INSERT_ID

Proves an unassigned int key is populated back through the UoW on MySQL. Store-
generated UUID stays PostgreSQL-only (no MySQL RETURNING)."
```

---

### Task 4: Finalize — full build and CHANGELOG

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Full solution build (0 warnings under TreatWarningsAsErrors)**

Run: `dotnet build Themia.sln --no-incremental`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (surfaces any `RS0016` PublicAPI gaps on the new package). If a warning/error appears, STOP and fix it before continuing.

- [ ] **Step 2: Add the CHANGELOG entry**

In `CHANGELOG.md`, under `## [Unreleased]`, add an `### Added` section (create it if absent) with:
```markdown
- **`Themia.Framework.Data.Dapper.MySql`** — MySQL engine for the Dapper data layer (`MySqlConnector` +
  SqlKata `MySqlCompiler`), registered via `AddThemiaDapperMySql`. Honours the full shared data-layer contract
  (tenant isolation, audit, soft-delete, unit of work) — proven by the conformance suite against `mysql:8.4`.
  `GuidFormat=Char36` is enforced for Guid keys; store-generated keys use `LAST_INSERT_ID()` (AUTO_INCREMENT
  integers; store-generated UUID remains PostgreSQL-only).
```

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): note the MySQL Dapper engine package"
```

---

## Self-Review

**1. Spec coverage:**
- Mirror PG engine (factory/compiler/DI) → Task 1. ✓
- `GuidFormat=Char36` enforced → Task 1 Step 2. ✓
- AUTO_INCREMENT int store-gen via `LAST_INSERT_ID`; uuid PG-only → Task 3 + documented in CHANGELOG. ✓
- Dapper-only conformance (full shared suite, `mysql:8.4`) → Task 2. ✓
- MySQL adaptations (Char36, `DATETIME(6)`, tz-naive) → factory (Char36), `MySqlDapperConfiguration` DateTimeOffset handler, fixture schema. ✓
- Add projects to sln; PublicAPI tracked; CHANGELOG → Tasks 1/2/4. ✓

**2. Placeholder scan:** No TBD/TODO; every code step has complete code. The two "if it fails" notes (Task 2 Step 5, Task 3 Step 2) are debugging guidance with the concrete fallback named, not placeholders — the happy path has full code and a definite expected result.

**3. Type consistency:** `AddThemiaDapperMySql` signature matches across the DI extension, PublicAPI.Unshipped, and both test call sites. `MySqlConnectionFactory`/`MySqlSqlCompiler`/`MySqlDapperConfiguration` names consistent. `Gadget : AuditableEntity<int>` resolved as `IRepository<Gadget, int>`. Fixture column names are snake_case matching the EntityMapping convention; `Widget`/`ConformanceScope` come from the shared Conformance project (same as the PG subclass). `MySqlGuidFormat.Char36`, `MySqlConnectionStringBuilder`, `MySqlCompiler` are MySqlConnector/SqlKata types.
