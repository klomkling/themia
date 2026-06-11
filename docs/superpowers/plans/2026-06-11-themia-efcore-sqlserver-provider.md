# Themia 0.4.5 — EF Core SQL Server provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a SQL Server EF Core provider as a first-class peer to the Postgres one, restructured into per-engine packages, with framework-column naming scoped so adopters get idiomatic PascalCase on SQL Server without breaking EF↔Dapper schema parity.

**Architecture:** Make `Themia.Framework.Data.EFCore` provider-agnostic; extract Postgres into `Themia.Framework.Data.EFCore.PostgreSql`; add `Themia.Framework.Data.EFCore.SqlServer`. Move the tenant-or-default connection-string resolution into a shared core helper. Make `ThemiaDbContext` own its framework column names explicitly (snake_case), and turn the global snake_case naming convention into an opt-in provider flag (default off) so adopter columns follow EF defaults.

**Tech Stack:** .NET 10, EF Core 10 (`Microsoft.EntityFrameworkCore.SqlServer`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `EFCore.NamingConventions`), xUnit, Testcontainers.MsSql, EF Core Sqlite (unit-test model introspection).

**Spec:** `docs/superpowers/specs/2026-06-11-themia-efcore-sqlserver-provider-design.md`

**Working directory:** all paths are relative to `Packages/themia/`. Build/test from there. The build enforces `TreatWarningsAsErrors=true`, `Nullable`, `GenerateDocumentationFile=true`, and tracks PublicAPI surface (`RS0016` for undocumented public members). Run `dotnet build Themia.sln --no-incremental` to surface PublicAPI diagnostics.

---

## File Structure

**Created:**
- `src/framework/Themia.Framework.Data.EFCore/Infrastructure/DatabaseConnectionStringResolver.cs` — shared tenant-or-default connection-string resolution (provider-agnostic).
- `src/framework/Themia.Framework.Data.EFCore.PostgreSql/` — new package: `PostgresDatabaseProvider.cs`, `DependencyInjection/PostgresServiceCollectionExtensions.cs`, `.csproj`, `PublicAPI.{Shipped,Unshipped}.txt`.
- `src/framework/Themia.Framework.Data.EFCore.SqlServer/` — new package: `SqlServerDatabaseProvider.cs`, `DependencyInjection/SqlServerServiceCollectionExtensions.cs`, `.csproj`, `PublicAPI.{Shipped,Unshipped}.txt`.
- `tests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests/` — new project: `SqlServerContainerFixture.cs`, ported concern suites, `NamingConventionTests.cs`, `.csproj`.

**Modified:**
- `Directory.Packages.props` — add `Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.Sqlite`.
- `src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs` — add explicit framework-column naming.
- `src/framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj` — drop Npgsql + NamingConventions; add IVT for the two provider packages.
- `src/framework/Themia.Framework.Data.EFCore/Extensions/ServiceCollectionExtensions.cs` — remove `AddThemiaPostgres`, `AddThemiaDbContextWithProvider`, `CreateProvider`.
- `src/framework/Themia.Framework.Data.EFCore/Providers/PostgresDatabaseProvider.cs` — **moved** to the PostgreSql package (file deleted here).
- `src/framework/Themia.Framework.Data.EFCore/PublicAPI.{Shipped,Unshipped}.txt` — remove moved/dropped members, add the resolver.
- `tests/Themia.Framework.Data.EFCore.Tests/` — retarget provider tests; reference the PostgreSql package; add Sqlite.
- `tests/Themia.Framework.Data.EFCore.IntegrationTests/Themia.Framework.Data.EFCore.IntegrationTests.csproj` — add explicit `EFCore.NamingConventions`.
- `Directory.Build.props`, `CHANGELOG.md`, `MIGRATION.md`, `Themia.sln` — release wiring.

---

## Task 1: Explicit framework-column naming in `ThemiaDbContext`

Make the framework own its column names explicitly (snake_case), so they no longer depend on a global naming convention. At this point the providers still apply the global convention, so existing behavior is unchanged (explicit `HasColumnName` and the convention agree).

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `tests/Themia.Framework.Data.EFCore.Tests/Themia.Framework.Data.EFCore.Tests.csproj`
- Create: `tests/Themia.Framework.Data.EFCore.Tests/Naming/FrameworkColumnNamingTests.cs`
- Modify: `src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs`

- [ ] **Step 1: Add the EF Sqlite test package to central management**

In `Directory.Packages.props`, under the EFCore block (after the `EFCore.NamingConventions` line), add:

```xml
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.8" />
```

- [ ] **Step 2: Reference Sqlite from the EFCore unit-test project**

In `tests/Themia.Framework.Data.EFCore.Tests/Themia.Framework.Data.EFCore.Tests.csproj`, add inside the package `ItemGroup` (next to the InMemory reference):

```xml
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" />
```

- [ ] **Step 3: Write the failing test**

Create `tests/Themia.Framework.Data.EFCore.Tests/Naming/FrameworkColumnNamingTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Naming;

public sealed class FrameworkColumnNamingTests
{
    // A framework entity that exercises every marker: key (Entity<int>), audit + soft-delete
    // (SoftDeletableEntity), tenant (ITenantEntity), concurrency (IConcurrencyAware), plus one
    // adopter-owned column (AppName) that Themia must NOT rename.
    private sealed class Probe : SoftDeletableEntity<int>, ITenantEntity, IConcurrencyAware
    {
        public TenantId? TenantId { get; set; }
        public byte[] RowVersion { get; set; } = [];
        public string AppName { get; set; } = string.Empty;
    }

    private sealed class ProbeContext(DbContextOptions options) : ThemiaDbContext(options)
    {
        public DbSet<Probe> Probes => Set<Probe>();
    }

    private static ProbeContext NewContext()
    {
        // SQLite gives a real relational model for column-name introspection, no global naming convention.
        var options = new DbContextOptionsBuilder<ProbeContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new ProbeContext(options);
    }

    private static string? ColumnOf(ProbeContext ctx, string property)
    {
        var entityType = ctx.Model.FindEntityType(typeof(Probe))!;
        var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)!.Value;
        return entityType.FindProperty(property)!.GetColumnName(store);
    }

    [Fact]
    public void FrameworkColumns_AreSnakeCase_WithoutGlobalConvention()
    {
        using var ctx = NewContext();

        Assert.Equal("id", ColumnOf(ctx, "Id"));
        Assert.Equal("tenant_id", ColumnOf(ctx, nameof(Probe.TenantId)));
        Assert.Equal("created_at", ColumnOf(ctx, "CreatedAt"));
        Assert.Equal("created_by", ColumnOf(ctx, "CreatedBy"));
        Assert.Equal("last_modified_at", ColumnOf(ctx, "LastModifiedAt"));
        Assert.Equal("last_modified_by", ColumnOf(ctx, "LastModifiedBy"));
        Assert.Equal("is_deleted", ColumnOf(ctx, "IsDeleted"));
        Assert.Equal("deleted_at", ColumnOf(ctx, "DeletedAt"));
        Assert.Equal("deleted_by", ColumnOf(ctx, "DeletedBy"));
        Assert.Equal("row_version", ColumnOf(ctx, nameof(Probe.RowVersion)));
    }

    [Fact]
    public void AdopterColumns_AreUntouched_WithoutGlobalConvention()
    {
        using var ctx = NewContext();

        // No global convention applied here, so the adopter's own column keeps its PascalCase name.
        Assert.Equal("AppName", ColumnOf(ctx, nameof(Probe.AppName)));
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~FrameworkColumnNamingTests"`
Expected: FAIL — `FrameworkColumns_AreSnakeCase_WithoutGlobalConvention` asserts `id`/`tenant_id`/etc. but gets PascalCase (`Id`/`TenantId`), because nothing maps them yet.

- [ ] **Step 5: Implement explicit framework-column naming**

In `src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs`, add the call inside `OnModelCreating` immediately after `ApplyTenantIdConversions(modelBuilder);`:

```csharp
        ApplyTenantIdConversions(modelBuilder);
        ApplyFrameworkColumnNames(modelBuilder);
```

Then add these members to the class (place them next to `ApplyTenantIdConversions`). The `using Themia.Framework.Core.Abstractions.Entities;` and `using Themia.Framework.Core.Abstractions.Tenancy;` directives already exist in this file.

```csharp
    // Framework columns Themia OWNS, mapped to fixed snake_case names so the EF and Dapper peers agree
    // per engine (Dapper's EntityMapping.ToSnakeCase produces the same names) and a single FluentMigrator
    // migration can serve both. Adopter-declared columns are never touched here.
    private static readonly (Type Marker, (string Property, string Column)[] Columns)[] FrameworkColumnMaps =
    [
        (typeof(ITenantEntity), [(nameof(ITenantEntity.TenantId), "tenant_id")]),
        (typeof(IAuditableEntity),
        [
            (nameof(IAuditableEntity.CreatedAt), "created_at"),
            (nameof(IAuditableEntity.CreatedBy), "created_by"),
            (nameof(IAuditableEntity.LastModifiedAt), "last_modified_at"),
            (nameof(IAuditableEntity.LastModifiedBy), "last_modified_by"),
        ]),
        (typeof(ISoftDeletable),
        [
            (nameof(ISoftDeletable.IsDeleted), "is_deleted"),
            (nameof(ISoftDeletable.DeletedAt), "deleted_at"),
            (nameof(ISoftDeletable.DeletedBy), "deleted_by"),
        ]),
        (typeof(IConcurrencyAware), [(nameof(IConcurrencyAware.RowVersion), "row_version")]),
    ];

    private static void ApplyFrameworkColumnNames(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            var entity = modelBuilder.Entity(clrType);

            // The key 'Id' is declared on the abstract base class Entity<TId> (there is no IEntity
            // interface), so map it only for entities that derive from Entity<>.
            if (DerivesFromEntityBase(clrType) && entityType.FindProperty("Id") is not null)
            {
                entity.Property("Id").HasColumnName("id");
            }

            foreach (var (marker, columns) in FrameworkColumnMaps)
            {
                if (!marker.IsAssignableFrom(clrType))
                {
                    continue;
                }

                foreach (var (property, column) in columns)
                {
                    if (entityType.FindProperty(property) is not null)
                    {
                        entity.Property(property).HasColumnName(column);
                    }
                }
            }
        }
    }

    // Walks the base-type chain looking for the open generic Themia base entity Entity<TId>.
    private static bool DerivesFromEntityBase(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Entity<>))
            {
                return true;
            }
        }

        return false;
    }
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~FrameworkColumnNamingTests"`
Expected: PASS (both tests).

- [ ] **Step 7: Run the full EFCore unit + integration suites to confirm no regression**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Framework.Data.EFCore"`
Expected: PASS. The integration tests apply `UseSnakeCaseNamingConvention()` themselves and now also get explicit framework mappings; both agree, so results are unchanged.

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props \
        tests/Themia.Framework.Data.EFCore.Tests/Themia.Framework.Data.EFCore.Tests.csproj \
        tests/Themia.Framework.Data.EFCore.Tests/Naming/FrameworkColumnNamingTests.cs \
        src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs
git commit -m "feat: map framework columns to explicit snake_case in ThemiaDbContext"
```

---

## Task 2: Extract connection-string resolution into a core helper

`PostgresDatabaseProvider.ResolveConnectionString` is `internal static` and is about to move out of core when Postgres is extracted. Both provider packages need it, so lift it into a provider-agnostic core helper first.

**Files:**
- Create: `src/framework/Themia.Framework.Data.EFCore/Infrastructure/DatabaseConnectionStringResolver.cs`
- Modify: `src/framework/Themia.Framework.Data.EFCore/Providers/PostgresDatabaseProvider.cs`
- Modify: `src/framework/Themia.Framework.Data.EFCore/PublicAPI.Unshipped.txt`
- Modify: `tests/Themia.Framework.Data.EFCore.Tests/Providers/PostgresDatabaseProviderTests.cs`

- [ ] **Step 1: Write the failing test**

Replace the body of `tests/Themia.Framework.Data.EFCore.Tests/Providers/PostgresDatabaseProviderTests.cs` so the resolver assertions target the new core helper. Keep the same scenarios (tenant cs wins, falls back to Default, throws when neither). The file currently calls `PostgresDatabaseProvider.ResolveConnectionString(...)`; change those calls to `DatabaseConnectionStringResolver.Resolve(...)` and update the class/namespace usings. Concretely, the resolver tests become:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Infrastructure;
using Themia.MultiTenancy.Abstractions;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Providers;

public sealed class DatabaseConnectionStringResolverTests
{
    private static IConfiguration ConfigWithDefault(string? value)
    {
        var data = new Dictionary<string, string?>();
        if (value is not null)
        {
            data["ConnectionStrings:Default"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private static IServiceProvider ServiceProviderWithTenant(string? tenantConnectionString)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantAccessor>(new StubTenantAccessor(tenantConnectionString));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolve_ReturnsTenantConnectionString_WhenPresent()
    {
        var sp = ServiceProviderWithTenant("Host=tenant");

        var result = DatabaseConnectionStringResolver.Resolve(ConfigWithDefault("Host=shared"), sp);

        Assert.Equal("Host=tenant", result);
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenNoTenantConnectionString()
    {
        var sp = ServiceProviderWithTenant(null);

        var result = DatabaseConnectionStringResolver.Resolve(ConfigWithDefault("Host=shared"), sp);

        Assert.Equal("Host=shared", result);
    }

    [Fact]
    public void Resolve_Throws_WhenNeitherTenantNorDefaultPresent()
    {
        var sp = ServiceProviderWithTenant(null);

        Assert.Throws<InvalidOperationException>(
            () => DatabaseConnectionStringResolver.Resolve(ConfigWithDefault(null), sp));
    }

    private sealed class StubTenantAccessor(string? connectionString) : ITenantAccessor
    {
        public TenantInfo? Current { get; } =
            connectionString is null ? null : new TenantInfo { ConnectionString = connectionString };
    }
}
```

> NOTE for the implementer: match the existing test's `ITenantAccessor`/`TenantInfo` shape — read the current `PostgresDatabaseProviderTests.cs` for the exact stub it uses (property names, whether `TenantInfo` is a record/has required members) and mirror it. Preserve any other (non-resolver) tests in that file unchanged; only the resolver-focused tests move to target the core helper.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~DatabaseConnectionStringResolverTests"`
Expected: FAIL to compile — `DatabaseConnectionStringResolver` does not exist yet.

- [ ] **Step 3: Create the core helper**

Create `src/framework/Themia.Framework.Data.EFCore/Infrastructure/DatabaseConnectionStringResolver.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.MultiTenancy.Abstractions;

namespace Themia.Framework.Data.EFCore.Infrastructure;

/// <summary>
/// Resolves the connection string for the current scope: the resolved tenant's connection string
/// (DB-per-tenant) when present, otherwise the configured <c>Default</c> connection string. Shared by
/// every Themia EF database provider so the resolution rule cannot drift between engines.
/// </summary>
public static class DatabaseConnectionStringResolver
{
    private const string DefaultConnectionName = "Default";

    /// <summary>
    /// Resolves the connection string: the resolved tenant's connection string when present, otherwise
    /// <c>Default</c>. Throws when neither is available.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="serviceProvider">Scoped service provider used to resolve <see cref="ITenantAccessor"/>.</param>
    /// <returns>The connection string to use for this scope.</returns>
    /// <exception cref="InvalidOperationException">Neither a tenant connection string nor a <c>Default</c> exists.</exception>
    public static string Resolve(IConfiguration configuration, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var tenantConnectionString = serviceProvider.GetService<ITenantAccessor>()?.Current?.ConnectionString;
        if (!string.IsNullOrWhiteSpace(tenantConnectionString))
        {
            return tenantConnectionString;
        }

        var defaultConnectionString = configuration.GetConnectionString(DefaultConnectionName);
        if (string.IsNullOrWhiteSpace(defaultConnectionString))
        {
            throw new InvalidOperationException(
                $"No tenant connection string was resolved and connection string '{DefaultConnectionName}' " +
                "was not found or is empty.");
        }

        return defaultConnectionString;
    }
}
```

- [ ] **Step 4: Point `PostgresDatabaseProvider` at the helper**

In `src/framework/Themia.Framework.Data.EFCore/Providers/PostgresDatabaseProvider.cs`: delete the private `ResolveConnectionString` method and the now-unused `DefaultConnectionName` constant, add `using Themia.Framework.Data.EFCore.Infrastructure;`, and change the call site in `Configure` to:

```csharp
        var connectionString = DatabaseConnectionStringResolver.Resolve(configuration, serviceProvider);
```

- [ ] **Step 5: Record the new public API**

Add to `src/framework/Themia.Framework.Data.EFCore/PublicAPI.Unshipped.txt`:

```
Themia.Framework.Data.EFCore.Infrastructure.DatabaseConnectionStringResolver
static Themia.Framework.Data.EFCore.Infrastructure.DatabaseConnectionStringResolver.Resolve(Microsoft.Extensions.Configuration.IConfiguration! configuration, System.IServiceProvider! serviceProvider) -> string!
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~DatabaseConnectionStringResolverTests"`
Expected: PASS.

Run: `dotnet build Themia.sln --no-incremental`
Expected: build succeeds with no `RS0016`/`RS0017` PublicAPI diagnostics.

- [ ] **Step 7: Commit**

```bash
git add src/framework/Themia.Framework.Data.EFCore/Infrastructure/DatabaseConnectionStringResolver.cs \
        src/framework/Themia.Framework.Data.EFCore/Providers/PostgresDatabaseProvider.cs \
        src/framework/Themia.Framework.Data.EFCore/PublicAPI.Unshipped.txt \
        tests/Themia.Framework.Data.EFCore.Tests/Providers/PostgresDatabaseProviderTests.cs
git commit -m "refactor: extract connection-string resolution into a shared core helper"
```

---

## Task 3: Extract the Postgres provider into its own package (+ opt-in naming flag)

Move `PostgresDatabaseProvider` and `AddThemiaPostgres` out of core into `Themia.Framework.Data.EFCore.PostgreSql`, leaving core provider-agnostic. Drop the global snake_case convention default (now an opt-in flag). Drop the string-name provider factory from core.

**Files:**
- Create: `src/framework/Themia.Framework.Data.EFCore.PostgreSql/Themia.Framework.Data.EFCore.PostgreSql.csproj`
- Create: `src/framework/Themia.Framework.Data.EFCore.PostgreSql/PostgresDatabaseProvider.cs`
- Create: `src/framework/Themia.Framework.Data.EFCore.PostgreSql/DependencyInjection/PostgresServiceCollectionExtensions.cs`
- Create: `src/framework/Themia.Framework.Data.EFCore.PostgreSql/PublicAPI.Shipped.txt` (empty)
- Create: `src/framework/Themia.Framework.Data.EFCore.PostgreSql/PublicAPI.Unshipped.txt`
- Delete: `src/framework/Themia.Framework.Data.EFCore/Providers/PostgresDatabaseProvider.cs`
- Modify: `src/framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj`
- Modify: `src/framework/Themia.Framework.Data.EFCore/Extensions/ServiceCollectionExtensions.cs`
- Modify: `src/framework/Themia.Framework.Data.EFCore/PublicAPI.Unshipped.txt`
- Modify: `tests/Themia.Framework.Data.EFCore.Tests/Themia.Framework.Data.EFCore.Tests.csproj`
- Modify: `tests/Themia.Framework.Data.EFCore.Tests/Providers/TenantConnectionRoutingTests.cs`
- Modify: `tests/Themia.Framework.Data.EFCore.IntegrationTests/Themia.Framework.Data.EFCore.IntegrationTests.csproj`
- Modify: `Themia.sln`

- [ ] **Step 1: Create the PostgreSql package project**

Create `src/framework/Themia.Framework.Data.EFCore.PostgreSql/Themia.Framework.Data.EFCore.PostgreSql.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Framework.Data.EFCore.PostgreSql</PackageId>
    <Description>PostgreSQL provider for the Themia EF Core data layer (Npgsql) with DB-per-tenant connection routing.</Description>
    <PackageTags>themia;efcore;data;postgres;npgsql;multi-tenancy</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj" />
    <ProjectReference Include="../Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="EFCore.NamingConventions" />
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

- [ ] **Step 2: Move `PostgresDatabaseProvider` into the package, gated by the opt-in flag**

Create `src/framework/Themia.Framework.Data.EFCore.PostgreSql/PostgresDatabaseProvider.cs` with the moved class. It now takes a `useGlobalSnakeCaseNaming` constructor flag and only applies the convention when set:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.Infrastructure;

namespace Themia.Framework.Data.EFCore.PostgreSql;

/// <summary>
/// PostgreSQL database provider using the Npgsql EF Core provider. Routes to the per-tenant connection
/// string when the resolved tenant carries one (DB-per-tenant), otherwise the <c>Default</c> connection
/// string (shared DB + the global tenant query filter).
/// </summary>
/// <param name="useGlobalSnakeCaseNaming">
/// When <c>true</c>, applies <c>UseSnakeCaseNamingConvention()</c> to the whole model (legacy behavior,
/// snake_cases the adopter's own columns too). Default <c>false</c>: only Themia's framework columns are
/// snake_case (mapped explicitly in <c>ThemiaDbContext</c>); the adopter's columns follow EF defaults.
/// </param>
public sealed class PostgresDatabaseProvider(bool useGlobalSnakeCaseNaming = false) : IDatabaseProvider
{
    /// <inheritdoc />
    public string ProviderName => DatabaseProviderNames.Postgres;

    /// <inheritdoc />
    public void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var connectionString = DatabaseConnectionStringResolver.Resolve(configuration, serviceProvider);

        optionsBuilder.UseNpgsql(connectionString, ConfigureNpgsqlOptions);

        if (useGlobalSnakeCaseNaming)
        {
            optionsBuilder.UseSnakeCaseNamingConvention();
        }
    }

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // No eager connection-string validation: with DB-per-tenant the connection string may be supplied
        // at request time via ITenantAccessor.Current.ConnectionString, so a missing "Default" is not
        // necessarily a misconfiguration. Resolution + validation happen per scope in Configure.
    }

    private static void ConfigureNpgsqlOptions(NpgsqlDbContextOptionsBuilder builder)
    {
        // Automatic transient-fault retry (EnableRetryOnFailure) is intentionally NOT configured: a
        // retrying execution strategy is incompatible with the user-initiated transactions exposed by
        // IUnitOfWork.BeginTransactionAsync (EF throws on BeginTransaction under a retrying strategy).
        // Hosts that need transient-fault resilience and do NOT use manual transactions can re-enable it
        // via the configureOptions delegate of AddThemiaPostgres.
    }
}
```

- [ ] **Step 3: Move `AddThemiaPostgres` into the package**

Create `src/framework/Themia.Framework.Data.EFCore.PostgreSql/DependencyInjection/PostgresServiceCollectionExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;

namespace Themia.Framework.Data.EFCore.PostgreSql;

/// <summary>
/// Registration extensions for the Themia PostgreSQL EF Core provider.
/// </summary>
public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Themia <see cref="ThemiaDbContext"/> with the PostgreSQL provider.
    /// </summary>
    /// <typeparam name="TContext">DbContext type derived from <see cref="ThemiaDbContext"/>.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="useGlobalSnakeCaseNaming">
    /// When <c>true</c>, snake_cases the entire model (legacy). Default <c>false</c>: only framework columns
    /// are snake_case; adopter columns follow EF defaults.
    /// </param>
    /// <param name="configureOptions">Optional DbContext options configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddThemiaPostgres<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useGlobalSnakeCaseNaming = false,
        Action<DbContextOptionsBuilder>? configureOptions = null)
        where TContext : ThemiaDbContext
    {
        var provider = new PostgresDatabaseProvider(useGlobalSnakeCaseNaming);
        return services.AddThemiaDbContext<TContext>(provider, configuration, configureOptions);
    }
}
```

- [ ] **Step 4: Delete the old provider file and prune core**

Delete `src/framework/Themia.Framework.Data.EFCore/Providers/PostgresDatabaseProvider.cs`:

```bash
git rm src/framework/Themia.Framework.Data.EFCore/Providers/PostgresDatabaseProvider.cs
```

In `src/framework/Themia.Framework.Data.EFCore/Extensions/ServiceCollectionExtensions.cs`: remove `using Themia.Framework.Data.EFCore.Providers;`, and delete the `AddThemiaPostgres<TContext>`, `AddThemiaDbContextWithProvider<TContext>`, and `CreateProvider` members. Keep the three provider-agnostic `AddThemiaDbContext<TContext>` overloads (the `(provider, …)`, the `(optionsAction)`, and any others that don't name a concrete provider).

- [ ] **Step 5: Drop the Npgsql + NamingConventions deps from core; add IVT for the provider packages**

In `src/framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj`: remove the `Npgsql.EntityFrameworkCore.PostgreSQL` and `EFCore.NamingConventions` `<PackageReference>` lines. Update `<PackageTags>` to drop `postgres`. Add InternalsVisibleTo entries for the provider packages alongside the existing test entries (the providers reference only public core types today, but this future-proofs internal sharing):

```xml
    <InternalsVisibleTo Include="Themia.Framework.Data.EFCore.PostgreSql" />
    <InternalsVisibleTo Include="Themia.Framework.Data.EFCore.SqlServer" />
```

- [ ] **Step 6: Update core PublicAPI — remove the moved/dropped members**

In `src/framework/Themia.Framework.Data.EFCore/PublicAPI.Unshipped.txt`, add removal markers for the members that left core (the analyzer requires shipped members removed from the surface to be recorded):

```
*REMOVED*static Themia.Framework.Data.EFCore.Extensions.ServiceCollectionExtensions.AddThemiaPostgres<TContext>(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Microsoft.Extensions.Configuration.IConfiguration! configuration, System.Action<Microsoft.EntityFrameworkCore.DbContextOptionsBuilder!>? configureOptions = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
*REMOVED*static Themia.Framework.Data.EFCore.Extensions.ServiceCollectionExtensions.AddThemiaDbContextWithProvider<TContext>(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Microsoft.Extensions.Configuration.IConfiguration! configuration, string! providerName, System.Action<Microsoft.EntityFrameworkCore.DbContextOptionsBuilder!>? configureOptions = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

> NOTE for the implementer: the exact signatures above must match the lines currently in `PublicAPI.Shipped.txt`. Open `src/framework/Themia.Framework.Data.EFCore/PublicAPI.Shipped.txt`, copy the two member lines verbatim, and prefix each with `*REMOVED*` in `PublicAPI.Unshipped.txt`. If `AddThemiaPostgres` is not in `Shipped.txt` (only `Unshipped`), delete it from `Unshipped` instead of adding a `*REMOVED*` marker. Same for `AddThemiaDbContextWithProvider`. (`PostgresDatabaseProvider` itself: if it was public and shipped, also add a `*REMOVED*` line for the type and its members.)

- [ ] **Step 7: Add the package's PublicAPI files**

Create `src/framework/Themia.Framework.Data.EFCore.PostgreSql/PublicAPI.Shipped.txt` as an **empty** file (matches the sibling packages' convention). Create `src/framework/Themia.Framework.Data.EFCore.PostgreSql/PublicAPI.Unshipped.txt`:

```
#nullable enable
Themia.Framework.Data.EFCore.PostgreSql.PostgresDatabaseProvider
Themia.Framework.Data.EFCore.PostgreSql.PostgresDatabaseProvider.PostgresDatabaseProvider(bool useGlobalSnakeCaseNaming = false) -> void
Themia.Framework.Data.EFCore.PostgreSql.PostgresDatabaseProvider.Configure(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder! optionsBuilder, Microsoft.Extensions.Configuration.IConfiguration! configuration, System.IServiceProvider! serviceProvider) -> void
Themia.Framework.Data.EFCore.PostgreSql.PostgresDatabaseProvider.ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Microsoft.Extensions.Configuration.IConfiguration! configuration) -> void
override Themia.Framework.Data.EFCore.PostgreSql.PostgresDatabaseProvider.ProviderName.get -> string!
Themia.Framework.Data.EFCore.PostgreSql.PostgresServiceCollectionExtensions
static Themia.Framework.Data.EFCore.PostgreSql.PostgresServiceCollectionExtensions.AddThemiaPostgres<TContext>(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Microsoft.Extensions.Configuration.IConfiguration! configuration, bool useGlobalSnakeCaseNaming = false, System.Action<Microsoft.EntityFrameworkCore.DbContextOptionsBuilder!>? configureOptions = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

> NOTE for the implementer: `ProviderName` is a get-only interface implementation. The exact PublicAPI line form (e.g. whether `ProviderName.get` is `override` or a plain `=>`) must match what a clean build reports. Build with `--no-incremental`, read the `RS0016` diagnostics, and reconcile these files until the build is clean — that is the source of truth for the exact strings.

- [ ] **Step 8: Update the EFCore.sln and consuming test projects**

Add the new project to `Themia.sln`:

```bash
dotnet sln Themia.sln add src/framework/Themia.Framework.Data.EFCore.PostgreSql/Themia.Framework.Data.EFCore.PostgreSql.csproj
```

In `tests/Themia.Framework.Data.EFCore.Tests/Themia.Framework.Data.EFCore.Tests.csproj`, add a project reference to the new package (its tests use `AddThemiaPostgres`):

```xml
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.PostgreSql/Themia.Framework.Data.EFCore.PostgreSql.csproj" />
```

In `tests/Themia.Framework.Data.EFCore.Tests/Providers/TenantConnectionRoutingTests.cs`:
- add `using Themia.Framework.Data.EFCore.PostgreSql;` (for `AddThemiaPostgres`);
- **delete** the `AddThemiaDbContextWithProvider_Throws_ForUnsupportedProviderName` test entirely — the method no longer exists, and its premise (sqlserver unsupported) is now false.

In `tests/Themia.Framework.Data.EFCore.IntegrationTests/Themia.Framework.Data.EFCore.IntegrationTests.csproj`, add an explicit `EFCore.NamingConventions` reference (previously transitive from core; the integration test contexts call `UseSnakeCaseNamingConvention()` directly):

```xml
    <PackageReference Include="EFCore.NamingConventions" />
```

- [ ] **Step 9: Build and test**

Run: `dotnet build Themia.sln --no-incremental`
Expected: succeeds; no `RS0016`/`RS0017` (reconcile PublicAPI files if any appear).

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Framework.Data.EFCore"`
Expected: PASS (unit + integration). The integration tests are unaffected (they configure Npgsql + snake_case themselves).

- [ ] **Step 10: Commit**

```bash
git add -A src/framework/Themia.Framework.Data.EFCore src/framework/Themia.Framework.Data.EFCore.PostgreSql \
        tests/Themia.Framework.Data.EFCore.Tests tests/Themia.Framework.Data.EFCore.IntegrationTests Themia.sln
git commit -m "refactor: extract Postgres EF provider into its own package; snake_case naming now opt-in"
```

---

## Task 4: Create the SQL Server provider package

Add `Themia.Framework.Data.EFCore.SqlServer` mirroring the PostgreSql package: `SqlServerDatabaseProvider` + `AddThemiaSqlServer`, the same opt-in naming flag, no retry strategy.

**Files:**
- Modify: `Directory.Packages.props`
- Create: `src/framework/Themia.Framework.Data.EFCore.SqlServer/Themia.Framework.Data.EFCore.SqlServer.csproj`
- Create: `src/framework/Themia.Framework.Data.EFCore.SqlServer/SqlServerDatabaseProvider.cs`
- Create: `src/framework/Themia.Framework.Data.EFCore.SqlServer/DependencyInjection/SqlServerServiceCollectionExtensions.cs`
- Create: `src/framework/Themia.Framework.Data.EFCore.SqlServer/PublicAPI.Shipped.txt` (empty)
- Create: `src/framework/Themia.Framework.Data.EFCore.SqlServer/PublicAPI.Unshipped.txt`
- Create: `tests/Themia.Framework.Data.EFCore.Tests/Providers/SqlServerDatabaseProviderTests.cs`
- Modify: `tests/Themia.Framework.Data.EFCore.Tests/Themia.Framework.Data.EFCore.Tests.csproj`
- Modify: `Themia.sln`

- [ ] **Step 1: Add the EF SqlServer package to central management**

In `Directory.Packages.props`, under the EFCore block, add:

```xml
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.8" />
```

- [ ] **Step 2: Create the SqlServer package project**

Create `src/framework/Themia.Framework.Data.EFCore.SqlServer/Themia.Framework.Data.EFCore.SqlServer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Framework.Data.EFCore.SqlServer</PackageId>
    <Description>SQL Server provider for the Themia EF Core data layer with DB-per-tenant connection routing.</Description>
    <PackageTags>themia;efcore;data;sqlserver;multi-tenancy</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj" />
    <ProjectReference Include="../Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
    <PackageReference Include="EFCore.NamingConventions" />
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

- [ ] **Step 3: Write the failing test**

Create `tests/Themia.Framework.Data.EFCore.Tests/Providers/SqlServerDatabaseProviderTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.SqlServer;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Providers;

public sealed class SqlServerDatabaseProviderTests
{
    [Fact]
    public void ProviderName_IsSqlServer()
    {
        Assert.Equal(DatabaseProviderNames.SqlServer, new SqlServerDatabaseProvider().ProviderName);
    }

    [Fact]
    public void AddThemiaSqlServer_RegistersContext()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Server=localhost;Database=themia;Trusted_Connection=True;TrustServerCertificate=True",
            })
            .Build();

        services.AddThemiaSqlServer<ProbeContext>(configuration);

        using var provider = services.BuildServiceProvider();
        // Resolving the context builds options through the provider (no DB connection is opened here).
        Assert.NotNull(provider.GetRequiredService<ProbeContext>());
    }

    [Fact]
    public void NamingSplit_FrameworkSnakeCase_AdopterPascalCase_OnSqlServer()
    {
        // Offline model introspection against the SQL Server provider (no connection opened).
        var options = new DbContextOptionsBuilder<ProbeContext>()
            .UseSqlServer("Server=localhost;Database=themia;TrustServerCertificate=True")
            .Options;
        using var ctx = new ProbeContext(options);

        var entityType = ctx.Model.FindEntityType(typeof(Probe))!;
        var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)!.Value;

        Assert.Equal("tenant_id", entityType.FindProperty(nameof(Probe.TenantId))!.GetColumnName(store));
        Assert.Equal("created_at", entityType.FindProperty("CreatedAt")!.GetColumnName(store));
        // Adopter column keeps PascalCase on SQL Server (no global convention by default).
        Assert.Equal("AppName", entityType.FindProperty(nameof(Probe.AppName))!.GetColumnName(store));
    }

    private sealed class Probe : SoftDeletableEntity<int>, ITenantEntity
    {
        public TenantId? TenantId { get; set; }
        public string AppName { get; set; } = string.Empty;
    }

    private sealed class ProbeContext(DbContextOptions options) : ThemiaDbContext(options)
    {
        public DbSet<Probe> Probes => Set<Probe>();
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~SqlServerDatabaseProviderTests"`
Expected: FAIL to compile — `SqlServerDatabaseProvider` / `AddThemiaSqlServer` do not exist; the test project does not yet reference the SqlServer package.

- [ ] **Step 5: Implement `SqlServerDatabaseProvider`**

Create `src/framework/Themia.Framework.Data.EFCore.SqlServer/SqlServerDatabaseProvider.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.Infrastructure;

namespace Themia.Framework.Data.EFCore.SqlServer;

/// <summary>
/// SQL Server database provider. Routes to the per-tenant connection string when the resolved tenant
/// carries one (DB-per-tenant), otherwise the <c>Default</c> connection string (shared DB + the global
/// tenant query filter).
/// </summary>
/// <param name="useGlobalSnakeCaseNaming">
/// When <c>true</c>, applies <c>UseSnakeCaseNamingConvention()</c> to the whole model. Default
/// <c>false</c>: only Themia's framework columns are snake_case (mapped in <c>ThemiaDbContext</c>); the
/// adopter's own columns follow EF defaults (PascalCase on SQL Server).
/// </param>
public sealed class SqlServerDatabaseProvider(bool useGlobalSnakeCaseNaming = false) : IDatabaseProvider
{
    /// <inheritdoc />
    public string ProviderName => DatabaseProviderNames.SqlServer;

    /// <inheritdoc />
    public void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var connectionString = DatabaseConnectionStringResolver.Resolve(configuration, serviceProvider);

        optionsBuilder.UseSqlServer(connectionString, ConfigureSqlServerOptions);

        if (useGlobalSnakeCaseNaming)
        {
            optionsBuilder.UseSnakeCaseNamingConvention();
        }
    }

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // No eager connection-string validation: with DB-per-tenant the connection string may be supplied
        // at request time via ITenantAccessor.Current.ConnectionString. Resolution + validation happen per
        // scope in Configure.
    }

    private static void ConfigureSqlServerOptions(SqlServerDbContextOptionsBuilder builder)
    {
        // Automatic transient-fault retry (EnableRetryOnFailure) is intentionally NOT configured: a
        // retrying execution strategy is incompatible with the user-initiated transactions exposed by
        // IUnitOfWork.BeginTransactionAsync (EF throws on BeginTransaction under a retrying strategy).
        // Hosts that need transient-fault resilience and do NOT use manual transactions can re-enable it
        // via the configureOptions delegate of AddThemiaSqlServer.
    }
}
```

> NOTE for the implementer: `SqlServerDbContextOptionsBuilder` lives in `Microsoft.EntityFrameworkCore.Infrastructure`. Confirm the exact namespace from the SqlServer provider package if the build flags the using.

- [ ] **Step 6: Implement `AddThemiaSqlServer`**

Create `src/framework/Themia.Framework.Data.EFCore.SqlServer/DependencyInjection/SqlServerServiceCollectionExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;

namespace Themia.Framework.Data.EFCore.SqlServer;

/// <summary>
/// Registration extensions for the Themia SQL Server EF Core provider.
/// </summary>
public static class SqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Themia <see cref="ThemiaDbContext"/> with the SQL Server provider.
    /// </summary>
    /// <typeparam name="TContext">DbContext type derived from <see cref="ThemiaDbContext"/>.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="useGlobalSnakeCaseNaming">
    /// When <c>true</c>, snake_cases the entire model. Default <c>false</c>: only framework columns are
    /// snake_case; adopter columns follow EF defaults (PascalCase on SQL Server).
    /// </param>
    /// <param name="configureOptions">Optional DbContext options configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddThemiaSqlServer<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useGlobalSnakeCaseNaming = false,
        Action<DbContextOptionsBuilder>? configureOptions = null)
        where TContext : ThemiaDbContext
    {
        var provider = new SqlServerDatabaseProvider(useGlobalSnakeCaseNaming);
        return services.AddThemiaDbContext<TContext>(provider, configuration, configureOptions);
    }
}
```

- [ ] **Step 7: Add PublicAPI files and wire up the projects**

Create `src/framework/Themia.Framework.Data.EFCore.SqlServer/PublicAPI.Shipped.txt` as an **empty** file. Create `src/framework/Themia.Framework.Data.EFCore.SqlServer/PublicAPI.Unshipped.txt`:

```
#nullable enable
Themia.Framework.Data.EFCore.SqlServer.SqlServerDatabaseProvider
Themia.Framework.Data.EFCore.SqlServer.SqlServerDatabaseProvider.SqlServerDatabaseProvider(bool useGlobalSnakeCaseNaming = false) -> void
Themia.Framework.Data.EFCore.SqlServer.SqlServerDatabaseProvider.Configure(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder! optionsBuilder, Microsoft.Extensions.Configuration.IConfiguration! configuration, System.IServiceProvider! serviceProvider) -> void
Themia.Framework.Data.EFCore.SqlServer.SqlServerDatabaseProvider.ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Microsoft.Extensions.Configuration.IConfiguration! configuration) -> void
override Themia.Framework.Data.EFCore.SqlServer.SqlServerDatabaseProvider.ProviderName.get -> string!
Themia.Framework.Data.EFCore.SqlServer.SqlServerServiceCollectionExtensions
static Themia.Framework.Data.EFCore.SqlServer.SqlServerServiceCollectionExtensions.AddThemiaSqlServer<TContext>(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, Microsoft.Extensions.Configuration.IConfiguration! configuration, bool useGlobalSnakeCaseNaming = false, System.Action<Microsoft.EntityFrameworkCore.DbContextOptionsBuilder!>? configureOptions = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

Add the project to the solution and reference it from the unit-test project:

```bash
dotnet sln Themia.sln add src/framework/Themia.Framework.Data.EFCore.SqlServer/Themia.Framework.Data.EFCore.SqlServer.csproj
```

In `tests/Themia.Framework.Data.EFCore.Tests/Themia.Framework.Data.EFCore.Tests.csproj`, add:

```xml
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.SqlServer/Themia.Framework.Data.EFCore.SqlServer.csproj" />
```

- [ ] **Step 8: Run the tests**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~SqlServerDatabaseProviderTests"`
Expected: PASS (all three).

Run: `dotnet build Themia.sln --no-incremental`
Expected: succeeds; reconcile the SqlServer `PublicAPI.Unshipped.txt` against any `RS0016` until clean.

- [ ] **Step 9: Commit**

```bash
git add Directory.Packages.props src/framework/Themia.Framework.Data.EFCore.SqlServer \
        tests/Themia.Framework.Data.EFCore.Tests Themia.sln
git commit -m "feat: add Themia.Framework.Data.EFCore.SqlServer provider package"
```

---

## Task 5: SQL Server integration tests (Testcontainers)

Verify the provider end-to-end against a real SQL Server: tenant isolation, audit stamping, soft-delete, concurrency (`rowversion`), and the naming split — built via `EnsureCreatedAsync()` (no FluentMigrator in this release).

**Files:**
- Create: `tests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.csproj`
- Create: `tests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests/SqlServerContainerFixture.cs`
- Create: `tests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests/NamingConventionTests.cs`
- Create: ported concern suites (tenant isolation, audit, soft-delete, concurrency)
- Modify: `Themia.sln`

- [ ] **Step 1: Create the integration-test project**

Create `tests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.csproj`:

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
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="Microsoft.Extensions.Configuration" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.SqlServer/Themia.Framework.Data.EFCore.SqlServer.csproj" />
    <ProjectReference Include="../../src/framework/Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the container fixture**

Create `tests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests/SqlServerContainerFixture.cs`. Mirror the Dapper SqlServer fixture's image choice. It exposes a factory that builds a test `ThemiaDbContext` (with an injected tenant context) against the container and runs `EnsureCreatedAsync()`.

```csharp
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests;

public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    public string ConnectionString => container.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        await container.StartAsync();

        // Create the schema once from the EF model.
        await using var ctx = CreateContext(tenant: null);
        await ctx.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    public TestDbContext CreateContext(TenantId? tenant) =>
        new(
            new DbContextOptionsBuilder<TestDbContext>().UseSqlServer(ConnectionString).Options,
            tenant is null ? null : new FixedTenantContext(tenant.Value));

    public async Task ResetAsync()
    {
        await using var ctx = CreateContext(tenant: null);
        await ctx.Database.ExecuteSqlRawAsync("DELETE FROM widgets");
    }

    private sealed class FixedTenantContext(TenantId tenant) : ITenantContext
    {
        public TenantId? CurrentTenantId { get; } = tenant;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerIntegrationCollection : ICollectionFixture<SqlServerContainerFixture>
{
    public const string Name = "SqlServerIntegration";
}
```

> NOTE for the implementer: read `tests/Themia.Framework.Data.Dapper.SqlServer.IntegrationTests/SqlServerContainerFixture.cs` for the exact `MsSqlBuilder` usage already proven in this repo (parameterless ctor + `.WithImage` is `[Obsolete]`/CS0618 under TreatWarningsAsErrors — use the image-string ctor as shown). Also confirm `ITenantContext`'s exact member shape from `Themia.Framework.Core.Abstractions.Tenancy` and match it.

- [ ] **Step 3: Define the test context and entity**

Add to the fixture file (or a `TestDbContext.cs`): a `Widget` entity exercising the markers and a `TestDbContext` exposing it.

```csharp
public sealed class Widget : Themia.Framework.Core.Abstractions.Entities.SoftDeletableEntity<int>,
    ITenantEntity,
    Themia.Framework.Core.Abstractions.Entities.IConcurrencyAware
{
    public TenantId? TenantId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public string Name { get; set; } = string.Empty;   // adopter-owned column (PascalCase on SQL Server)
}

public sealed class TestDbContext(DbContextOptions options, ITenantContext? tenant)
    : Themia.Framework.Data.EFCore.ThemiaDbContext(options, tenant)
{
    public DbSet<Widget> Widgets => Set<Widget>();
}
```

- [ ] **Step 4: Write the naming-split integration test**

Create `tests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests/NamingConventionTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests;

[Collection(SqlServerIntegrationCollection.Name)]
public sealed class NamingConventionTests(SqlServerContainerFixture fixture)
{
    [Fact]
    public async Task FrameworkColumns_AreSnakeCase_AdopterColumn_IsPascalCase()
    {
        await using var ctx = fixture.CreateContext(tenant: null);

        var columns = await ctx.Database
            .SqlQuery<string>($"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'widgets'")
            .ToListAsync();

        Assert.Contains("tenant_id", columns);
        Assert.Contains("created_at", columns);
        Assert.Contains("is_deleted", columns);
        Assert.Contains("row_version", columns);
        Assert.Contains("id", columns);
        // Adopter column keeps PascalCase on SQL Server (no global convention by default).
        Assert.Contains("Name", columns);
    }
}
```

> NOTE for the implementer: the table name `widgets` follows from the `Widgets` DbSet under EF's default table naming on SQL Server — confirm the actual table name `EnsureCreated` produces (query `INFORMATION_SCHEMA.TABLES`) and use it consistently in the fixture's `ResetAsync` and here. If EF names it `Widgets`, adjust both.

- [ ] **Step 5: Port the behavior suites from the Postgres integration tests**

Create four test classes mirroring the existing Postgres EF integration suites, each annotated `[Collection(SqlServerIntegrationCollection.Name)]` and using `fixture.CreateContext(tenant)` + `fixture.ResetAsync()`:

- `TenantIsolationTests.cs` — port `tests/Themia.Framework.Data.EFCore.IntegrationTests/Tenancy/PostgresTenantIsolationTests.cs`: a tenant only sees its own rows; cross-tenant `Find` returns null; no-tenant context sees only global rows; cross-tenant write throws `ConcurrencyException`.
- `AuditTrackingTests.cs` — port `Audit/AuditTrackingIntegrationTests.cs`: `created_at` set on insert, `last_modified_at` on update.
- `SoftDeleteTests.cs` — port `SoftDelete/SoftDeleteIntegrationTests.cs`: delete flips `is_deleted`; filtered out of queries.
- `ConcurrencyTests.cs` — port `Concurrency/ConcurrencyIntegrationTests.cs`: concurrent update throws `DbUpdateConcurrencyException` via the `rowversion` token.

For each: copy the Postgres test's assertions verbatim; change only (a) the fixture/container (use `SqlServerContainerFixture` + `CreateContext`), and (b) any Postgres-specific SQL/type assertions (e.g. `xmin`/`bytea` notes do not apply — SQL Server uses server-maintained `rowversion`, already handled in `ThemiaDbContext.ApplyConcurrencyTokens`). Keep the same behavioral expectations — they are engine-agnostic because the logic lives in `ThemiaDbContext`.

> NOTE for the implementer: read each Postgres source suite in full before porting; reproduce its scenarios, not a subset. Where a Postgres test references a Postgres-only detail, translate it to the SQL Server equivalent rather than dropping the assertion.

- [ ] **Step 6: Register the project and run**

```bash
dotnet sln Themia.sln add tests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.csproj
```

Run (requires Docker): `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Framework.Data.EFCore.SqlServer.IntegrationTests"`
Expected: PASS — container starts, schema created, all suites green.

- [ ] **Step 7: Commit**

```bash
git add tests/Themia.Framework.Data.EFCore.SqlServer.IntegrationTests Themia.sln
git commit -m "test: add EF SQL Server integration suite (Testcontainers) incl. naming split"
```

---

## Task 6: Release wiring

Bump the version, document the changes, and finalize the PublicAPI surface.

**Files:**
- Modify: `Directory.Build.props`
- Modify: `CHANGELOG.md`
- Modify/Create: `MIGRATION.md`
- Verify: all `PublicAPI.{Shipped,Unshipped}.txt`
- Verify: `Themia.sln`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>0.4.4</Version>` to `<Version>0.4.5</Version>`.

- [ ] **Step 2: Update the changelog**

In `CHANGELOG.md`, add at the top (above `## 0.4.4`):

```markdown
## 0.4.5 — 2026-06-11

### Added
- `Themia.Framework.Data.EFCore.SqlServer` — SQL Server EF Core provider (`AddThemiaSqlServer`,
  `SqlServerDatabaseProvider`) with DB-per-tenant connection routing; a first-class peer to the
  Postgres provider over the same `ThemiaDbContext` behavior.
- `Themia.Framework.Data.EFCore.PostgreSql` — the PostgreSQL provider, extracted from the core
  package into its own per-engine package (mirrors the Dapper layer topology).
- `DatabaseConnectionStringResolver` — shared tenant-or-default connection-string resolution in core.

### Changed
- `Themia.Framework.Data.EFCore` is now **provider-agnostic** — it no longer references Npgsql or a
  naming-convention package. Consumers reference the per-engine provider package they use.
- Framework columns (audit/tenant/soft-delete/concurrency + the entity key) are now mapped to explicit
  snake_case names in `ThemiaDbContext`, independent of any global naming convention.
- The global snake_case naming convention is now **opt-in** via `useGlobalSnakeCaseNaming` (default
  `false`) on `AddThemiaPostgres` / `AddThemiaSqlServer`. By default, an adopter's own columns follow
  EF's default convention (PascalCase on SQL Server); framework columns remain snake_case.

### Removed
- `ServiceCollectionExtensions.AddThemiaDbContextWithProvider` (string-name provider factory) — use the
  per-engine `AddThemiaPostgres` / `AddThemiaSqlServer` entry points.
```

- [ ] **Step 3: Add migration notes**

Append to `MIGRATION.md` (create it with an `# Themia migration notes` heading if it does not exist):

```markdown
## 0.4.4 → 0.4.5

- **`AddThemiaPostgres` moved packages.** It now lives in `Themia.Framework.Data.EFCore.PostgreSql`.
  Add a package reference to `Themia.Framework.Data.EFCore.PostgreSql` and a
  `using Themia.Framework.Data.EFCore.PostgreSql;`.
- **`AddThemiaDbContextWithProvider` removed.** Call `AddThemiaPostgres` or `AddThemiaSqlServer` directly.
- **App-column naming changed (Postgres).** The EF layer no longer forces snake_case on your own
  entities by default — only Themia's framework columns are snake_case. To restore the previous
  behavior (snake_case for all columns), pass `useGlobalSnakeCaseNaming: true` to `AddThemiaPostgres`.
- **New: SQL Server provider.** Reference `Themia.Framework.Data.EFCore.SqlServer` and call
  `AddThemiaSqlServer<TContext>(configuration)`. Your own columns default to PascalCase on SQL Server;
  Themia's framework columns are snake_case (matching the Dapper SQL Server engine).
```

- [ ] **Step 4: Finalize the PublicAPI surface**

Run: `dotnet build Themia.sln --no-incremental`
Expected: no `RS0016` (undocumented public member) or `RS0017` (removed-but-still-listed) diagnostics across all packages. Reconcile the `PublicAPI.{Shipped,Unshipped}.txt` files until clean. (Shipped/Unshipped promotion happens at release packaging time per existing repo practice; do not hand-promote here unless the build requires it.)

- [ ] **Step 5: Full build and test**

Run: `dotnet build Themia.sln --no-incremental`
Run: `dotnet test Themia.sln` (requires Docker for the integration suites)
Expected: clean build; all suites green.

- [ ] **Step 6: Commit**

```bash
git add Directory.Build.props CHANGELOG.md MIGRATION.md \
        src/framework/Themia.Framework.Data.EFCore*/PublicAPI.*.txt
git commit -m "chore: release 0.4.5 — EF SQL Server provider + per-engine package split"
```

---

## Self-Review notes (for the author; not execution steps)

- **Spec coverage:** §1 topology → Tasks 3, 4 (+ core prune). §2 provider → Task 4. §3 scoped naming → Task 1 (mapping) + Tasks 3/4 (opt-in flag) + Task 5 (end-to-end split). §4 engine specifics → inherited (concurrency note in Task 5). §5 testing → Tasks 1, 4, 5. §6 release → Task 6. Connection-string sharing (implied by the split) → Task 2.
- **Type consistency:** `useGlobalSnakeCaseNaming` (bool, default false) used identically on both providers and both `AddThemia*` methods; `DatabaseConnectionStringResolver.Resolve(IConfiguration, IServiceProvider)` used by both providers; `ApplyFrameworkColumnNames` / `DerivesFromEntityBase` defined and called in Task 1; `DatabaseProviderNames.{Postgres,SqlServer}` already exist in core.
- **Known soft spots flagged inline for the implementer (read-existing-file, not placeholders):** exact PublicAPI line strings (reconcile against `--no-incremental` RS0016 output — the build is the source of truth); the `ITenantContext`/`ITenantAccessor`/`TenantInfo` member shapes (mirror existing tests/fixtures); the `MsSqlBuilder` image-ctor form (copy the proven Dapper SqlServer fixture); the EnsureCreated table name (`widgets` vs `Widgets` — verify via INFORMATION_SCHEMA and use consistently).
```
