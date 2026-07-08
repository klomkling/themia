# Themia.Modules.Pdf Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the deferred tenant-aware PDF **template store** + **render-by-key** service on top of the shipped neutral `Themia.Pdf` renderer, on both data peers (EF Core + Dapper), all three engines (MySQL via Dapper only).

**Architecture:** One module package `Themia.Modules.Pdf` (net10.0). A single `PdfTemplate` entity over one FluentMigrator schema, read/written through an EF store and a Dapper store that both enforce tenant isolation + soft-delete + audit + global-default fallback via the framework data layers. A `PdfDocumentRenderer` composes the store's resolve with the neutral `IHtmlTemplateRenderer`/`IPdfRenderer`. A one-line framework prerequisite (Task 0) adds a per-query global-inclusion override to the sanctioned Dapper query path.

**Tech Stack:** C# / .NET 10, EF Core 10, Dapper + SqlKata, FluentMigrator, Handlebars.Net + PuppeteerSharp (via `Themia.Pdf`), xUnit, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-07-07-themia-modules-pdf-design.md`

## Global Constraints

- Target framework `net10.0` for the module (module convention). Framework Dapper change (Task 0) stays on that package's existing TFMs (`net8.0;net10.0`).
- `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true` are enforced by `Directory.Build.props` — warnings fail the build.
- Central package management: versions live in `Directory.Packages.props`; `.csproj` carries `<PackageReference Include="..." />` with no `Version`.
- Cross-cutting packages track PublicAPI (`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`); undocumented public members surface as `RS0016` on a clean build. All new public members get XML docs.
- `System.Text.Json` only — never `Newtonsoft.Json`.
- Log via `ILogger<T>` only — no `Console.*`. Log once per handled exception.
- Table name is `pdf_templates` (no schema) — this equals the Dapper convention `Pluralize(ToSnakeCase("PdfTemplate"))`, so EF and Dapper agree with no Dapper mapping override, and it sidesteps cross-engine schema qualification (MySQL has no schemas).
- Framework column names are fixed snake_case and owned by the framework: `id`, `tenant_id`, `created_at`, `created_by`, `last_modified_at`, `last_modified_by`, `is_deleted`, `deleted_at`, `deleted_by`, `restored_at`, `restored_by`.
- Build: `dotnet build Themia.sln`; test: `dotnet test Themia.sln`. Single class: `dotnet test Themia.sln --filter <ClassName>`.

---

## File Structure

**Framework prerequisite (Task 0) — `src/framework/Themia.Framework.Data.Dapper/`:**
- Modify: `Tenancy/ITenantQueryFactory.cs` — add `For<T>(bool includeGlobalRecords)`.
- Modify: `Tenancy/TenantQueryFactory.cs` — implement the overload; `For<T>()` delegates to it.
- Modify: `PublicAPI.Unshipped.txt` — record the new interface member.
- Test: `tests/Themia.Framework.Data.Dapper.Tests/Tenancy/TenantQueryFactoryTests.cs`.

**Module — `src/modules/Themia.Modules.Pdf/`:**
- Create: `Themia.Modules.Pdf.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`.
- Create: `PdfTemplate.cs` — the entity.
- Create: `TemplateNotFoundException.cs`.
- Create: `Store/IPdfTemplateStore.cs`.
- Create: `Store/PdfTemplateResolver.cs` — shared prefer-tenant-over-global tiebreak (used by both stores).
- Create: `PdfDbContext.cs`, `EntityConfiguration/PdfTemplateConfiguration.cs`.
- Create: `Store/EfPdfTemplateStore.cs`, `Store/DapperPdfTemplateStore.cs`.
- Create: `Rendering/IPdfDocumentRenderer.cs`, `Rendering/PdfDocumentRenderer.cs`.
- Create: `Migrations/PdfTemplateSchemaMigration.cs`.
- Create: `PdfModuleOptions.cs`, `PdfModule.cs`.
- Create: `DependencyInjection/PdfModuleServiceCollectionExtensions.cs`.

**Tests:**
- Create: `tests/Themia.Modules.Pdf.Tests/` (unit) — resolver precedence, renderer composition, DI lifetimes.
- Create: `tests/Themia.Modules.Pdf.IntegrationTests/` (Testcontainers) — migration/schema, both stores per engine, write asymmetry, Dapper global fallback, end-to-end render.

---

## Task 0: Framework prerequisite — per-query global-inclusion override on the Dapper query factory

**Files:**
- Modify: `src/framework/Themia.Framework.Data.Dapper/Tenancy/ITenantQueryFactory.cs`
- Modify: `src/framework/Themia.Framework.Data.Dapper/Tenancy/TenantQueryFactory.cs`
- Modify: `src/framework/Themia.Framework.Data.Dapper/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Framework.Data.Dapper.Tests/Tenancy/TenantQueryFactoryTests.cs`

**Interfaces:**
- Produces: `ITenantQueryFactory.For<T>(bool includeGlobalRecords) : SqlKata.Query` — a query pre-seeded with the tenant predicate where global (`tenant_id IS NULL`) inclusion is forced to the passed value, overriding `DapperDataOptions.IncludeGlobalRecordsForTenants`. Consumed by `DapperPdfTemplateStore` (Task 5).

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Data.Dapper.Tests/Tenancy/TenantQueryFactoryTests.cs`:

```csharp
using SqlKata.Compilers;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Dapper;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Tenancy;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests.Tenancy;

public sealed class TenantQueryFactoryTests
{
    private sealed record Widget { public Guid Id { get; init; } public TenantId? TenantId { get; init; } }

    private static TenantQueryFactory Factory(bool appOptionIncludesGlobals) =>
        new(new EntityMappingRegistry(),
            new FakeTenantContext(new TenantId("acme")),
            new DataFilterScope(),
            new DapperDataOptions { IncludeGlobalRecordsForTenants = appOptionIncludesGlobals });

    [Fact]
    public void For_with_includeGlobalRecords_true_emits_is_null_clause_even_when_app_option_is_false()
    {
        var sql = new SqlServerCompiler().Compile(Factory(false).For<Widget>(includeGlobalRecords: true)).ToString();
        Assert.Contains("Is Null", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void For_with_includeGlobalRecords_false_omits_is_null_clause_even_when_app_option_is_true()
    {
        var sql = new SqlServerCompiler().Compile(Factory(true).For<Widget>(includeGlobalRecords: false)).ToString();
        Assert.DoesNotContain("Is Null", sql, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeTenantContext(TenantId? id) : ITenantContext
    {
        public TenantId? CurrentTenantId { get; } = id;
    }
}
```

> Note: `TenantQueryFactory` is `internal`; the test relies on the existing `InternalsVisibleTo("Themia.Framework.Data.Dapper.Tests")` in the Dapper project (add it if missing). `ITenantContext` has a single member `TenantId? CurrentTenantId` — confirm against `Themia.Framework.Core/Abstractions/Tenancy` and adjust the fake if the interface has more members.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Themia.sln --filter TenantQueryFactoryTests`
Expected: FAIL — `For<T>(bool)` does not exist (compile error).

- [ ] **Step 3: Add the interface overload**

In `ITenantQueryFactory.cs`, add the second method (keep the XML doc):

```csharp
public interface ITenantQueryFactory
{
    /// <summary>Returns a tenant- and soft-delete-seeded query for <typeparamref name="T"/>,
    /// using the app-wide <see cref="DapperDataOptions.IncludeGlobalRecordsForTenants"/> for global inclusion.</summary>
    Query For<T>();

    /// <summary>As <see cref="For{T}()"/>, but overrides global (null-tenant) inclusion for this query only —
    /// letting a caller opt a single query into (or out of) the global fallback regardless of the app-wide default.</summary>
    Query For<T>(bool includeGlobalRecords);
}
```

- [ ] **Step 4: Implement the overload**

Replace the body of `TenantQueryFactory.For<T>()`:

```csharp
public Query For<T>() => For<T>(options.IncludeGlobalRecordsForTenants);

public Query For<T>(bool includeGlobalRecords)
{
    var map = registry.For<T>();
    var query = new Query(map.Table);
    TenantPredicate.Apply<T>(
        query,
        tenantContext.CurrentTenantId,
        includeGlobalRecords,
        filterScope.IsTenantFilterBypassed,
        filterScope.IsSoftDeleteFilterBypassed,
        map);
    return query;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Themia.sln --filter TenantQueryFactoryTests`
Expected: PASS (both facts).

- [ ] **Step 6: Record PublicAPI + commit**

Add to `src/framework/Themia.Framework.Data.Dapper/PublicAPI.Unshipped.txt`:

```
Themia.Framework.Data.Dapper.Tenancy.ITenantQueryFactory.For<T>(bool includeGlobalRecords) -> SqlKata.Query
```

Run: `dotnet build Themia.sln --no-incremental` (expect no `RS0016`), then:

```bash
git add src/framework/Themia.Framework.Data.Dapper tests/Themia.Framework.Data.Dapper.Tests
git commit -m "feat: add ITenantQueryFactory.For<T>(includeGlobalRecords) per-query global override"
```

---

## Task 1: Scaffold the module project + `PdfTemplate` entity + `TemplateNotFoundException`

**Files:**
- Create: `src/modules/Themia.Modules.Pdf/Themia.Modules.Pdf.csproj`
- Create: `src/modules/Themia.Modules.Pdf/PublicAPI.Shipped.txt` (empty), `PublicAPI.Unshipped.txt`
- Create: `src/modules/Themia.Modules.Pdf/PdfTemplate.cs`
- Create: `src/modules/Themia.Modules.Pdf/TemplateNotFoundException.cs`
- Create: `tests/Themia.Modules.Pdf.Tests/Themia.Modules.Pdf.Tests.csproj`
- Test: `tests/Themia.Modules.Pdf.Tests/PdfTemplateTests.cs`
- Modify: `Themia.sln` (add both projects)

**Interfaces:**
- Produces: `Themia.Modules.Pdf.PdfTemplate` — `sealed class : SoftDeletableEntity<Guid>, ITenantEntity` with `TenantId? TenantId`, `required string Key`, `required string Body`, `string? Name`, `string? Description`. (`IAuditableEntity` is inherited via `AuditableEntity<Guid>` — do not redeclare it.)
- Produces: `Themia.Modules.Pdf.TemplateNotFoundException : Exception` — thrown by resolve; HTTP-agnostic (no `StatusCode`).

- [ ] **Step 1: Create the module `.csproj`**

`src/modules/Themia.Modules.Pdf/Themia.Modules.Pdf.csproj` (mirror `Themia.Modules.Export.csproj`, minus Quartz/Export refs, plus the neutral Pdf ref):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Pdf</PackageId>
    <Description>Tenant-aware PDF/HTML template store with global-default fallback and render-by-key over Themia.Pdf. EF Core + Dapper peers.</Description>
    <PackageTags>themia;pdf;templates;multitenancy;html</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../framework/Themia.Framework.Core/Themia.Framework.Core.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj" />
    <ProjectReference Include="../../neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
    <ProjectReference Include="../../neutral/Themia.Pdf/Themia.Pdf.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="FluentMigrator" />
    <PackageReference Include="Dapper" />
    <PackageReference Include="SqlKata" />
    <PackageReference Include="EFCore.NamingConventions" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Modules.Pdf.Tests" />
    <InternalsVisibleTo Include="Themia.Modules.Pdf.IntegrationTests" />
  </ItemGroup>
</Project>
```

Create empty `PublicAPI.Shipped.txt`. Create `PublicAPI.Unshipped.txt` containing `#nullable enable` on the first line (mirror an existing module's file). Confirm `SqlKata` is pinned in `Directory.Packages.props` (it is used by the Dapper layer); if it is not directly referenceable, drop the explicit `SqlKata` line — it flows transitively from `Themia.Framework.Data.Dapper`. Verify by building.

- [ ] **Step 2: Write the failing test**

`tests/Themia.Modules.Pdf.Tests/Themia.Modules.Pdf.Tests.csproj` — mirror `tests/Themia.Modules.Export.Tests` csproj (xUnit + reference to the module). Then `PdfTemplateTests.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Pdf;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfTemplateTests
{
    [Fact]
    public void Global_template_has_null_tenant()
    {
        var t = new PdfTemplate { Key = "invoice", Body = "<p>{{Total}}</p>", TenantId = null };
        Assert.Null(t.TenantId);
        Assert.False(t.IsDeleted);
    }

    [Fact]
    public void Not_found_exception_is_http_agnostic()
    {
        var ex = new TemplateNotFoundException("invoice");
        Assert.Contains("invoice", ex.Message);
        Assert.Null(ex.GetType().GetProperty("StatusCode")); // no HTTP coupling
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test Themia.sln --filter PdfTemplateTests`
Expected: FAIL — `PdfTemplate` / `TemplateNotFoundException` do not exist.

- [ ] **Step 4: Create the entity and exception**

`PdfTemplate.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Pdf;

/// <summary>A stored HTML/Handlebars template resolved per tenant with a global-default fallback.</summary>
public sealed class PdfTemplate : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <summary>Owning tenant; <c>null</c> for a global default template.</summary>
    public TenantId? TenantId { get; set; }

    /// <summary>Resolution key (e.g. "invoice"). Unique per tenant, and once globally.</summary>
    public required string Key { get; set; }

    /// <summary>The Handlebars/HTML template source rendered against a model.</summary>
    public required string Body { get; set; }

    /// <summary>Human-readable label for management UIs.</summary>
    public string? Name { get; set; }

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }
}
```

`TemplateNotFoundException.cs`:

```csharp
namespace Themia.Modules.Pdf;

/// <summary>Thrown when no tenant-owned or global template exists for a requested key.
/// HTTP-agnostic — an ASP.NET Core middleware owns any status mapping (project convention).</summary>
public sealed class TemplateNotFoundException(string key)
    : Exception($"No PDF template found for key '{key}' (tenant-owned or global).")
{
    /// <summary>The unresolved template key.</summary>
    public string Key { get; } = key;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Themia.sln --filter PdfTemplateTests`
Expected: PASS.

- [ ] **Step 6: Record PublicAPI + commit**

Add to `PublicAPI.Unshipped.txt` (under `#nullable enable`):

```
Themia.Modules.Pdf.PdfTemplate
Themia.Modules.Pdf.PdfTemplate.PdfTemplate() -> void
Themia.Modules.Pdf.PdfTemplate.TenantId.get -> Themia.Framework.Core.Abstractions.Tenancy.TenantId?
Themia.Modules.Pdf.PdfTemplate.TenantId.set -> void
Themia.Modules.Pdf.PdfTemplate.Key.get -> string!
Themia.Modules.Pdf.PdfTemplate.Key.set -> void
Themia.Modules.Pdf.PdfTemplate.Body.get -> string!
Themia.Modules.Pdf.PdfTemplate.Body.set -> void
Themia.Modules.Pdf.PdfTemplate.Name.get -> string?
Themia.Modules.Pdf.PdfTemplate.Name.set -> void
Themia.Modules.Pdf.PdfTemplate.Description.get -> string?
Themia.Modules.Pdf.PdfTemplate.Description.set -> void
Themia.Modules.Pdf.TemplateNotFoundException
Themia.Modules.Pdf.TemplateNotFoundException.TemplateNotFoundException(string! key) -> void
Themia.Modules.Pdf.TemplateNotFoundException.Key.get -> string!
```

Run `dotnet build Themia.sln --no-incremental` and reconcile any `RS0016` by copying the exact suggested signature from the diagnostic. Add both projects to `Themia.sln` (`dotnet sln Themia.sln add src/modules/Themia.Modules.Pdf/Themia.Modules.Pdf.csproj tests/Themia.Modules.Pdf.Tests/Themia.Modules.Pdf.Tests.csproj`). Commit:

```bash
git add src/modules/Themia.Modules.Pdf tests/Themia.Modules.Pdf.Tests Themia.sln
git commit -m "feat: scaffold Themia.Modules.Pdf with PdfTemplate entity"
```

---

## Task 2: `IPdfTemplateStore` contract + `PdfTemplateResolver` (prefer-tenant tiebreak)

**Files:**
- Create: `src/modules/Themia.Modules.Pdf/Store/IPdfTemplateStore.cs`
- Create: `src/modules/Themia.Modules.Pdf/Store/PdfTemplateResolver.cs`
- Test: `tests/Themia.Modules.Pdf.Tests/PdfTemplateResolverTests.cs`

**Interfaces:**
- Produces: `IPdfTemplateStore` with `Task<PdfTemplate> CreateAsync(PdfTemplate, CancellationToken)`, `Task<PdfTemplate> UpdateAsync(PdfTemplate, CancellationToken)`, `Task DeleteAsync(Guid id, CancellationToken)`, `Task<PdfTemplate?> GetAsync(Guid id, CancellationToken)`, `Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken)`, `Task<PdfTemplate> ResolveAsync(string key, CancellationToken)`. Consumed by both stores (Tasks 4, 5) and `PdfDocumentRenderer` (Task 6).
- Produces: `static PdfTemplate? PdfTemplateResolver.PreferTenant(IEnumerable<PdfTemplate> candidates)` — from a key-filtered candidate set (≤1 tenant row + ≤1 global row), returns the tenant-owned row if present, else the global, else null. Consumed by both stores.

- [ ] **Step 1: Write the failing test**

`PdfTemplateResolverTests.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Pdf.Store;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfTemplateResolverTests
{
    private static PdfTemplate Tpl(string? tenant) =>
        new() { Key = "invoice", Body = "b", TenantId = tenant is null ? null : new TenantId(tenant) };

    [Fact]
    public void Prefers_tenant_row_over_global()
    {
        var chosen = PdfTemplateResolver.PreferTenant([Tpl("acme"), Tpl(null)]);
        Assert.Equal(new TenantId("acme"), chosen!.TenantId);
    }

    [Fact]
    public void Falls_back_to_global_when_no_tenant_row()
    {
        var chosen = PdfTemplateResolver.PreferTenant([Tpl(null)]);
        Assert.Null(chosen!.TenantId);
    }

    [Fact]
    public void Returns_null_when_no_candidates()
    {
        Assert.Null(PdfTemplateResolver.PreferTenant([]));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Themia.sln --filter PdfTemplateResolverTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Create the contract and resolver**

`Store/IPdfTemplateStore.cs`:

```csharp
namespace Themia.Modules.Pdf.Store;

/// <summary>Tenant-aware CRUD + key resolution for <see cref="PdfTemplate"/>.</summary>
public interface IPdfTemplateStore
{
    /// <summary>Persists a new template. Under a tenant scope the row is tenant-owned; a global
    /// (null-tenant) template can only be created from a no-tenant (system) scope.</summary>
    Task<PdfTemplate> CreateAsync(PdfTemplate template, CancellationToken cancellationToken = default);

    /// <summary>Updates an existing template within the current scope.</summary>
    Task<PdfTemplate> UpdateAsync(PdfTemplate template, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes the template with <paramref name="id"/> within the current scope.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Gets a template by id within the current scope (tenant-owned or global), or null.</summary>
    Task<PdfTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lists templates visible to the current scope (the tenant's own plus global defaults).</summary>
    Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves a template by key: the tenant's own row if present, else the global default,
    /// else throws <see cref="TemplateNotFoundException"/>.</summary>
    Task<PdfTemplate> ResolveAsync(string key, CancellationToken cancellationToken = default);
}
```

`Store/PdfTemplateResolver.cs`:

```csharp
namespace Themia.Modules.Pdf.Store;

/// <summary>Shared resolution tiebreak so the EF and Dapper stores pick identically.</summary>
internal static class PdfTemplateResolver
{
    /// <summary>From a key-filtered candidate set (at most one tenant row and one global row),
    /// returns the tenant-owned row if present, else the global, else null.</summary>
    public static PdfTemplate? PreferTenant(IEnumerable<PdfTemplate> candidates)
    {
        PdfTemplate? global = null;
        foreach (var c in candidates)
        {
            if (c.TenantId is not null)
            {
                return c; // tenant-owned wins outright
            }
            global ??= c;
        }
        return global;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Themia.sln --filter PdfTemplateResolverTests`
Expected: PASS.

- [ ] **Step 5: Record PublicAPI + commit**

Add the `IPdfTemplateStore` members to `PublicAPI.Unshipped.txt` (build once and copy the exact `RS0016` signatures — `PdfTemplateResolver` is `internal`, so it does not appear). Commit:

```bash
git add src/modules/Themia.Modules.Pdf tests/Themia.Modules.Pdf.Tests
git commit -m "feat: add IPdfTemplateStore contract and shared resolution tiebreak"
```

---

## Task 3: `PdfDbContext` + EF entity configuration

**Files:**
- Create: `src/modules/Themia.Modules.Pdf/PdfDbContext.cs`
- Create: `src/modules/Themia.Modules.Pdf/EntityConfiguration/PdfTemplateConfiguration.cs`
- Test: `tests/Themia.Modules.Pdf.Tests/PdfDbContextModelTests.cs`

**Interfaces:**
- Produces: `PdfDbContext : ThemiaDbContext` with `DbSet<PdfTemplate> Templates`. Consumed by `EfPdfTemplateStore` (Task 4) and DI (Task 7). Table `pdf_templates`; adopter columns `key`, `body`, `name`, `description`; framework columns via the base context. `IncludeGlobalRecordsForTenants` inherits `true` (safe — context holds only `PdfTemplate`).

- [ ] **Step 1: Write the failing test**

`PdfDbContextModelTests.cs` (uses the EF InMemory provider only to inspect the built model — not for query semantics):

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Modules.Pdf;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfDbContextModelTests
{
    private static PdfDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<PdfDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PdfDbContext(options);
    }

    [Fact]
    public void Maps_pdf_template_to_expected_table_and_columns()
    {
        using var ctx = NewContext();
        var entity = ctx.Model.FindEntityType(typeof(PdfTemplate))!;
        Assert.Equal("pdf_templates", entity.GetTableName());
        Assert.Null(entity.GetSchema());
        Assert.Equal("tenant_id", entity.FindProperty("TenantId")!.GetColumnName());
        Assert.Equal("key", entity.FindProperty("Key")!.GetColumnName());
        Assert.Equal("body", entity.FindProperty("Body")!.GetColumnName());
    }
}
```

> The `PdfDbContext` needs a constructor taking `DbContextOptions<PdfDbContext>` (+ optional `ITenantContext`), mirroring `ExportDbContext`. `Microsoft.EntityFrameworkCore.InMemory` must be referenced by the test project — add the package if the test csproj lacks it.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Themia.sln --filter PdfDbContextModelTests`
Expected: FAIL — `PdfDbContext` does not exist.

- [ ] **Step 3: Create the context and configuration**

`PdfDbContext.cs` (mirror `ExportDbContext`):

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Pdf.EntityConfiguration;

namespace Themia.Modules.Pdf;

/// <summary>EF context for the PDF module's template table. Tenant, soft-delete and global-inclusion
/// filters come from <see cref="ThemiaDbContext"/>.</summary>
public sealed class PdfDbContext : ThemiaDbContext
{
    /// <summary>Creates the context. Tenant is resolved from the injected accessor.</summary>
    public PdfDbContext(DbContextOptions<PdfDbContext> options, ITenantContext? tenantContext = null)
        : base(options, tenantContext)
    {
    }

    /// <summary>The stored templates.</summary>
    public DbSet<PdfTemplate> Templates => Set<PdfTemplate>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PdfTemplateConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
```

`EntityConfiguration/PdfTemplateConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Themia.Modules.Pdf.EntityConfiguration;

internal sealed class PdfTemplateConfiguration : IEntityTypeConfiguration<PdfTemplate>
{
    public void Configure(EntityTypeBuilder<PdfTemplate> b)
    {
        b.ToTable("pdf_templates");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(100);
        b.Property(x => x.Key).IsRequired().HasMaxLength(200);
        b.Property(x => x.Body).IsRequired();
        b.Property(x => x.Name).HasMaxLength(400);
        // Adopter column names map to snake_case explicitly so the EF and Dapper peers agree.
        b.Property(x => x.Key).HasColumnName("key");
        b.Property(x => x.Body).HasColumnName("body");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Themia.sln --filter PdfDbContextModelTests`
Expected: PASS.

- [ ] **Step 5: Record PublicAPI + commit**

Add `PdfDbContext` public members to `PublicAPI.Unshipped.txt` (copy exact `RS0016` signatures — `PdfTemplateConfiguration` is `internal`). Commit:

```bash
git add src/modules/Themia.Modules.Pdf tests/Themia.Modules.Pdf.Tests
git commit -m "feat: add PdfDbContext and EF entity configuration"
```

---

## Task 4: `EfPdfTemplateStore` (writes through EfUnitOfWork)

**Files:**
- Create: `src/modules/Themia.Modules.Pdf/Store/EfPdfTemplateStore.cs`
- Test: `tests/Themia.Modules.Pdf.IntegrationTests/EfPdfTemplateStoreTests.cs`

**Interfaces:**
- Consumes: `PdfDbContext` (Task 3), `IPdfTemplateStore` (Task 2), `PdfTemplateResolver.PreferTenant` (Task 2), `EfUnitOfWork` (framework, public), `IDataFilterScope` + `ISqlExceptionInterpreter` (framework, registered).
- Produces: `EfPdfTemplateStore : IPdfTemplateStore` (internal). Consumed by DI (Task 7).

**Design notes for the implementer:**
- Reads use the context's `Templates` DbSet — the base `ThemiaDbContext` filter auto-adds `tenant = @t OR tenant IS NULL` and `is_deleted = false`, so `ResolveAsync` queries `Where(x => x.Key == key)`, materializes the ≤2 rows, and calls `PdfTemplateResolver.PreferTenant`.
- Writes go through `new EfUnitOfWork(db, filterScope, interpreter).SaveChangesAsync(ct)` — **not** `db.SaveChangesAsync` — so `ValidateTenantWritesAsync` runs and the tenant/global write asymmetry holds (Export's own store skips this; do not copy that).
- Integration test uses a real engine (PostgreSQL Testcontainer) — mirror the container fixture in `tests/Themia.Modules.Export.IntegrationTests` (schema applied via the migration from Task 6; if Task 6 is not yet merged when running this task standalone, apply the DDL through the same `ThemiaMigrations.Run` call the fixture uses). The InMemory provider cannot enforce tenant filters or unique indexes, so store behavior is verified on a real engine.

- [ ] **Step 1: Write the failing test**

`EfPdfTemplateStoreTests.cs` (sketch — fill the fixture from the Export integration fixture):

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Pdf.Store;
using Xunit;

namespace Themia.Modules.Pdf.IntegrationTests;

[Collection("PostgresPdf")] // reuse the shared Postgres container collection
public sealed class EfPdfTemplateStoreTests(PdfPostgresFixture fx)
{
    [Fact]
    public async Task Resolve_prefers_tenant_template_over_global()
    {
        // Arrange: seed a global "invoice" (system scope) and an "acme" override (tenant scope)
        await fx.AsSystem(async store => await store.CreateAsync(
            new PdfTemplate { Key = "invoice", Body = "GLOBAL", TenantId = null }));
        await fx.AsTenant("acme", async store => await store.CreateAsync(
            new PdfTemplate { Key = "invoice", Body = "ACME", TenantId = null })); // stamped to acme on write

        // Act + Assert
        await fx.AsTenant("acme", async store =>
        {
            var t = await store.ResolveAsync("invoice");
            Assert.Equal("ACME", t.Body);
        });
        await fx.AsTenant("other", async store =>
        {
            var t = await store.ResolveAsync("invoice");
            Assert.Equal("GLOBAL", t.Body); // falls back to global
        });
    }

    [Fact]
    public async Task Resolve_throws_when_no_tenant_or_global_template()
    {
        await fx.AsTenant("acme", async store =>
            await Assert.ThrowsAsync<TemplateNotFoundException>(() => store.ResolveAsync("missing")));
    }

    [Fact]
    public async Task Tenant_scope_cannot_create_a_global_row()
    {
        // A tenant creating TenantId=null yields a tenant-owned row, never a global one.
        await fx.AsTenant("acme", async store =>
            await store.CreateAsync(new PdfTemplate { Key = "note", Body = "x", TenantId = null }));
        await fx.AsSystem(async store =>
        {
            var globals = await store.ListAsync();
            Assert.DoesNotContain(globals, t => t is { Key: "note", TenantId: null });
        });
    }
}
```

> `PdfPostgresFixture` (a new fixture in the integration project) builds a service provider with `AddThemiaPdfModuleEfCore` (Task 7) pointed at the container, runs the migration, and exposes `AsTenant(tenantId, useStore)` / `AsSystem(useStore)` helpers that create a DI scope with the ambient tenant set (mirror how the Export integration tests set `BackgroundTenantScope`/`AmbientTenantContext`). Its `PdfPostgresFixture` + `[CollectionDefinition("PostgresPdf")]` mirror the Export fixture.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Themia.sln --filter EfPdfTemplateStoreTests`
Expected: FAIL — `EfPdfTemplateStore` / fixture do not exist.

- [ ] **Step 3: Implement the store**

`Store/EfPdfTemplateStore.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.EFCore.UnitOfWork;

namespace Themia.Modules.Pdf.Store;

internal sealed class EfPdfTemplateStore(
    PdfDbContext db,
    IDataFilterScope filterScope,
    ISqlExceptionInterpreter interpreter) : IPdfTemplateStore
{
    // A fresh EfUnitOfWork per save is cheap (wraps the scoped context) and routes writes through
    // ValidateTenantWritesAsync so the tenant/global write asymmetry is enforced.
    private EfUnitOfWork UoW => new(db, filterScope, interpreter);

    public async Task<PdfTemplate> CreateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        if (template.Id == Guid.Empty)
        {
            template = WithId(template, Guid.NewGuid());
        }
        db.Templates.Add(template);
        await UoW.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template;
    }

    public async Task<PdfTemplate> UpdateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        db.Templates.Update(template);
        await UoW.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await db.Templates.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return; // idempotent delete
        }
        db.Templates.Remove(existing); // ThemiaDbContext converts hard delete to soft delete
        await UoW.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PdfTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await db.Templates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
        await db.Templates.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<PdfTemplate> ResolveAsync(string key, CancellationToken cancellationToken = default)
    {
        var candidates = await db.Templates.AsNoTracking()
            .Where(x => x.Key == key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return PdfTemplateResolver.PreferTenant(candidates) ?? throw new TemplateNotFoundException(key);
    }

    // Id has a protected setter on Entity<TId>; set it via the required-init clone helper. Because the
    // caller-supplied instance already carries Key/Body, reuse it and assign Id through reflection-free
    // means: expose Id assignment by giving PdfTemplate an internal initializer, OR (simpler) require
    // callers to leave Id default and assign here through a small internal helper on the entity.
    private static PdfTemplate WithId(PdfTemplate t, Guid id)
    {
        t.GetType().GetProperty(nameof(PdfTemplate.Id))!.SetValue(t, id);
        return t;
    }
}
```

> **Implementer note (id assignment):** `Entity<TId>.Id` has a `protected set`. The reflection in `WithId` violates the project's "avoid reflection in business logic" rule. Prefer one of: (a) add an `internal` method `PdfTemplate.AssignId(Guid)` on the entity that sets `Id`, or (b) have the DB generate the key. Pick (a) — add `internal void AssignId(Guid id) => Id = id;` to `PdfTemplate` (Task 1 follow-up, note it in that entity) and replace `WithId` with `template.AssignId(Guid.NewGuid())`. Confirm how `ExportRunStore` assigns ids and mirror it.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Themia.sln --filter EfPdfTemplateStoreTests`
Expected: PASS (requires Docker for Testcontainers).

- [ ] **Step 5: Commit**

```bash
git add src/modules/Themia.Modules.Pdf tests/Themia.Modules.Pdf.IntegrationTests
git commit -m "feat: add EF PdfTemplate store with UoW-guarded writes and global fallback"
```

---

## Task 5: `DapperPdfTemplateStore` (reads via the Task 0 override, writes via IRepository + IUnitOfWork)

**Files:**
- Create: `src/modules/Themia.Modules.Pdf/Store/DapperPdfTemplateStore.cs`
- Test: `tests/Themia.Modules.Pdf.IntegrationTests/DapperPdfTemplateStoreTests.cs`

**Interfaces:**
- Consumes: `ITenantQueryFactory` (with the Task 0 `For<T>(bool)` override), `ISqlCompiler`, `IDapperConnectionContext`, `IRepository<PdfTemplate, Guid>`, `IUnitOfWork` — all registered by `AddThemiaDapperCore` (the app wires the framework Dapper layer + a provider package). `PdfTemplateResolver.PreferTenant` (Task 2).
- Produces: `DapperPdfTemplateStore : IPdfTemplateStore` (internal). Consumed by DI (Task 7).

**Design notes:**
- **Reads** build the query via `ITenantQueryFactory.For<PdfTemplate>(includeGlobalRecords: true)` so the global fallback is included **regardless** of the app-wide `DapperDataOptions.IncludeGlobalRecordsForTenants` default (the finding-#1 fix). Resolve adds `.Where("key", key)`; List adds nothing; Get adds `.Where("id", id)`. Compile with `ISqlCompiler`, execute with Dapper's `QueryAsync<PdfTemplate>` on `IDapperConnectionContext.GetOpenConnectionAsync` joining `CurrentTransaction`. Then `PdfTemplateResolver.PreferTenant` for resolve.
- **Writes** use `IRepository<PdfTemplate, Guid>` (`AddAsync` / `Update` / `Remove`) then `IUnitOfWork.SaveChangesAsync` — `DapperUnitOfWork` stamps `tenant_id` from the ambient tenant on insert (so a tenant creating `TenantId=null` gets a tenant-owned row) and scopes update/delete to the ambient tenant.
- Column name for `key` in the WHERE is `map.Column("Key")` == `"key"` (convention), but since the store builds SqlKata directly, pass the snake_case column string literals matching the migration: `"key"`, `"id"`.

- [ ] **Step 1: Write the failing test**

`DapperPdfTemplateStoreTests.cs` — the critical one is the global-fallback-with-app-option-OFF:

```csharp
using Themia.Modules.Pdf.Store;
using Xunit;

namespace Themia.Modules.Pdf.IntegrationTests;

[Collection("MySqlPdf")]
public sealed class DapperPdfTemplateStoreTests(PdfMySqlFixture fx)
{
    [Fact]
    public async Task Resolves_global_default_even_though_app_option_excludes_globals()
    {
        // fx wires AddThemiaDapperCore WITHOUT setting IncludeGlobalRecordsForTenants (default false).
        await fx.AsSystem(async store => await store.CreateAsync(
            new PdfTemplate { Key = "invoice", Body = "GLOBAL", TenantId = null }));

        await fx.AsTenant("acme", async store =>
        {
            var t = await store.ResolveAsync("invoice"); // must find the global via For<T>(true)
            Assert.Equal("GLOBAL", t.Body);
        });
    }

    [Fact]
    public async Task Crud_roundtrips_on_mysql()
    {
        await fx.AsTenant("acme", async store =>
        {
            var created = await store.CreateAsync(new PdfTemplate { Key = "c", Body = "v" });
            var got = await store.GetAsync(created.Id);
            Assert.Equal("v", got!.Body);
            await store.DeleteAsync(created.Id);
            Assert.Null(await store.GetAsync(created.Id)); // soft-deleted, filtered out
        });
    }
}
```

> `PdfMySqlFixture` mirrors `PdfPostgresFixture` but uses a MySQL Testcontainer and `AddThemiaPdfModuleDapper` + `AddThemiaDapperCore` + the MySQL Dapper provider package, deliberately leaving `IncludeGlobalRecordsForTenants` at its `false` default to prove the override. Also add a `[Collection("PostgresPdfDapper")]` and `[Collection("SqlServerPdfDapper")]` variant (or a `[Theory]` over engine fixtures) so the Dapper store is exercised on all three engines per the spec.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Themia.sln --filter DapperPdfTemplateStoreTests`
Expected: FAIL — `DapperPdfTemplateStore` / fixture do not exist.

- [ ] **Step 3: Implement the store**

`Store/DapperPdfTemplateStore.cs`:

```csharp
using global::Dapper;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Sql;
using Themia.Framework.Data.Dapper.Tenancy;

namespace Themia.Modules.Pdf.Store;

internal sealed class DapperPdfTemplateStore(
    ITenantQueryFactory queries,
    ISqlCompiler compiler,
    IDapperConnectionContext connection,
    IRepository<PdfTemplate, Guid> repository,
    IUnitOfWork unitOfWork) : IPdfTemplateStore
{
    public async Task<PdfTemplate> CreateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        if (template.Id == Guid.Empty)
        {
            template.AssignId(Guid.NewGuid()); // internal setter added in Task 1 follow-up
        }
        await repository.AddAsync(template, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template;
    }

    public async Task<PdfTemplate> UpdateAsync(PdfTemplate template, CancellationToken cancellationToken = default)
    {
        repository.Update(template);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var existing = await GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }
        repository.Remove(existing);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<PdfTemplate?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        QueryFirstAsync(queries.For<PdfTemplate>(includeGlobalRecords: true).Where("id", id).Limit(1), cancellationToken);

    public async Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
        await QueryListAsync(queries.For<PdfTemplate>(includeGlobalRecords: true), cancellationToken).ConfigureAwait(false);

    public async Task<PdfTemplate> ResolveAsync(string key, CancellationToken cancellationToken = default)
    {
        var candidates = await QueryListAsync(
            queries.For<PdfTemplate>(includeGlobalRecords: true).Where("key", key), cancellationToken).ConfigureAwait(false);
        return PdfTemplateResolver.PreferTenant(candidates) ?? throw new TemplateNotFoundException(key);
    }

    private async Task<IReadOnlyList<PdfTemplate>> QueryListAsync(SqlKata.Query query, CancellationToken cancellationToken)
    {
        var sql = compiler.Compile(query);
        var conn = await connection.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await conn.QueryAsync<PdfTemplate>(
            new CommandDefinition(sql.Sql, sql.Parameters, connection.CurrentTransaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    private async Task<PdfTemplate?> QueryFirstAsync(SqlKata.Query query, CancellationToken cancellationToken)
    {
        var sql = compiler.Compile(query);
        var conn = await connection.GetOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await conn.QueryFirstOrDefaultAsync<PdfTemplate>(
            new CommandDefinition(sql.Sql, sql.Parameters, connection.CurrentTransaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
```

> **Dapper materialization note:** confirm that Dapper maps the `tenant_id` string column back to `PdfTemplate.TenantId` (`TenantId?`). The Dapper layer registers type handlers via `DapperConfiguration.EnsureConfigured()` (called by `AddThemiaDapperCore`) — verify a `TenantId` type handler exists (the framework has one, since other entities round-trip `TenantId`). If materialization of the `required` `Key`/`Body` members fails under Dapper, confirm the Dapper layer's mapping strategy for `required` members (it uses convention property mapping); mirror whatever the framework's Dapper tests do for a `required`-member entity.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Themia.sln --filter DapperPdfTemplateStoreTests`
Expected: PASS on all three engine fixtures (requires Docker).

- [ ] **Step 5: Commit**

```bash
git add src/modules/Themia.Modules.Pdf tests/Themia.Modules.Pdf.IntegrationTests
git commit -m "feat: add Dapper PdfTemplate store with per-query global fallback (all 3 engines)"
```

---

## Task 6: FluentMigrator schema (`pdf_templates`, all three engines, uniqueness via raw SQL)

**Files:**
- Create: `src/modules/Themia.Modules.Pdf/Migrations/PdfTemplateSchemaMigration.cs`
- Test: `tests/Themia.Modules.Pdf.IntegrationTests/PdfSchemaMigrationTests.cs`

**Interfaces:**
- Produces: `PdfTemplateSchemaMigration : FluentMigrator.Migration` (public, discovered by `ThemiaMigrations.Run(engine, connectionString, typeof(PdfTemplateSchemaMigration).Assembly)` in Task 7). Creates table `pdf_templates` (no schema) with framework + adopter columns, and the uniqueness indexes.

**Uniqueness rule:** one template per `(tenant_id, key)`, and exactly one global per `key`. Because engines disagree on NULL semantics in unique indexes, use two indexes via raw SQL per `IfDatabase`:
- `ux_pdf_templates_tenant_key` — unique on `(tenant_id, key)` **where `tenant_id IS NOT NULL`** (per-tenant uniqueness).
- `ux_pdf_templates_global_key` — unique on `(key)` **where `tenant_id IS NULL`** (single global per key).

This two-partial-index form works on all three engines without `NULLS NOT DISTINCT` (avoids the PG-15 dependency). MySQL 8.0.13+ supports functional/partial-equivalent indexes; if the target MySQL predates expression indexes, emulate the global-uniqueness index with a generated column (`tenant_key_global` = `key` when `tenant_id IS NULL`) — decide against the deployment's MySQL version and document it in the migration.

- [ ] **Step 1: Write the failing test**

`PdfSchemaMigrationTests.cs`:

```csharp
using Themia.Modules.Pdf.Store;
using Xunit;

namespace Themia.Modules.Pdf.IntegrationTests;

[Collection("PostgresPdf")]
public sealed class PdfSchemaMigrationTests(PdfPostgresFixture fx)
{
    [Fact]
    public async Task Duplicate_global_key_is_rejected()
    {
        await fx.AsSystem(async store => await store.CreateAsync(new PdfTemplate { Key = "dup", Body = "a", TenantId = null }));
        await fx.AsSystem(async store =>
            await Assert.ThrowsAnyAsync<Exception>(() => store.CreateAsync(new PdfTemplate { Key = "dup", Body = "b", TenantId = null })));
    }

    [Fact]
    public async Task Same_key_allowed_for_different_tenants_and_one_global()
    {
        await fx.AsSystem(async s => await s.CreateAsync(new PdfTemplate { Key = "shared", Body = "g", TenantId = null }));
        await fx.AsTenant("t1", async s => await s.CreateAsync(new PdfTemplate { Key = "shared", Body = "1" }));
        await fx.AsTenant("t2", async s => await s.CreateAsync(new PdfTemplate { Key = "shared", Body = "2" }));
        // No exception: one global + one per tenant all coexist.
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Themia.sln --filter PdfSchemaMigrationTests`
Expected: FAIL — migration/table does not exist.

- [ ] **Step 3: Implement the migration**

`Migrations/PdfTemplateSchemaMigration.cs` (mirror `NotificationsSchemaMigration`'s 3-engine `DateTimeType` handling; columns mirror `ExportSchemaMigration`'s framework columns):

```csharp
using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Modules.Pdf.Migrations;

[Migration(202607070001, "Themia.Pdf: create pdf_templates table")]
public sealed class PdfTemplateSchemaMigration : Migration
{
    private delegate ICreateTableColumnOptionOrWithColumnSyntax DateTimeType(ICreateTableColumnAsTypeSyntax column);

    public override void Up()
    {
        IfDatabase("postgresql").Delegate(() => CreateTable(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTable(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTable(c => c.AsDateTimeOffset()));

        // Two partial unique indexes (raw SQL — filtered indexes are not expressible via the fluent API).
        IfDatabase("postgresql").Delegate(() =>
        {
            Execute.Sql("CREATE UNIQUE INDEX ux_pdf_templates_tenant_key ON pdf_templates (tenant_id, key) WHERE tenant_id IS NOT NULL;");
            Execute.Sql("CREATE UNIQUE INDEX ux_pdf_templates_global_key ON pdf_templates (key) WHERE tenant_id IS NULL;");
        });
        IfDatabase("sqlserver").Delegate(() =>
        {
            Execute.Sql("CREATE UNIQUE INDEX ux_pdf_templates_tenant_key ON pdf_templates (tenant_id, [key]) WHERE tenant_id IS NOT NULL;");
            Execute.Sql("CREATE UNIQUE INDEX ux_pdf_templates_global_key ON pdf_templates ([key]) WHERE tenant_id IS NULL;");
        });
        IfDatabase("mysql").Delegate(() =>
        {
            // MySQL 8.0.13+ functional index emulating "unique per (tenant,key) where tenant not null"
            // and "one global per key". Verify the target MySQL version supports expression indexes.
            Execute.Sql("CREATE UNIQUE INDEX ux_pdf_templates_tenant_key ON pdf_templates ((IF(tenant_id IS NULL, NULL, tenant_id)), (IF(tenant_id IS NULL, NULL, `key`)));");
            Execute.Sql("CREATE UNIQUE INDEX ux_pdf_templates_global_key ON pdf_templates ((IF(tenant_id IS NULL, `key`, NULL)));");
        });

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Modules.Pdf supports only PostgreSQL, MySQL, and SQL Server."));
    }

    public override void Down() => Delete.Table("pdf_templates");

    private void CreateTable(DateTimeType dt)
    {
        var t = Create.Table("pdf_templates")
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("key").AsString(200).NotNullable()
            .WithColumn("body").AsString(int.MaxValue).NotNullable()
            .WithColumn("name").AsString(400).Nullable()
            .WithColumn("description").AsString(int.MaxValue).Nullable();
        dt(t.WithColumn("created_at")).NotNullable();
        t.WithColumn("created_by").AsString(100).Nullable();
        dt(t.WithColumn("last_modified_at")).Nullable();
        t.WithColumn("last_modified_by").AsString(100).Nullable();
        t.WithColumn("is_deleted").AsBoolean().NotNullable().WithDefaultValue(false);
        dt(t.WithColumn("deleted_at")).Nullable();
        t.WithColumn("deleted_by").AsString(100).Nullable();
        dt(t.WithColumn("restored_at")).Nullable();
        t.WithColumn("restored_by").AsString(100).Nullable();
    }
}
```

> Verify `key` is not a reserved word that breaks unquoted DDL on MySQL/Postgres — quote it (`` `key` `` / `"key"` / `[key]`) in the raw index SQL as shown, and confirm the FluentMigrator column builder emits a quoted identifier for the column definition on each engine (it does for MySQL/SqlServer; on Postgres `key` is allowed unquoted but the index SQL uses it bare — quote if the build complains).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Themia.sln --filter "PdfSchemaMigrationTests|DapperPdfTemplateStoreTests|EfPdfTemplateStoreTests"`
Expected: PASS on all engine fixtures.

- [ ] **Step 5: Commit**

```bash
git add src/modules/Themia.Modules.Pdf tests/Themia.Modules.Pdf.IntegrationTests
git commit -m "feat: add pdf_templates FluentMigrator schema with per-engine uniqueness"
```

---

## Task 7: `IPdfDocumentRenderer` + `PdfDocumentRenderer` (render-by-key)

**Files:**
- Create: `src/modules/Themia.Modules.Pdf/Rendering/IPdfDocumentRenderer.cs`
- Create: `src/modules/Themia.Modules.Pdf/Rendering/PdfDocumentRenderer.cs`
- Test: `tests/Themia.Modules.Pdf.Tests/PdfDocumentRendererTests.cs`

**Interfaces:**
- Consumes: `IPdfTemplateStore` (Task 2), `IHtmlTemplateRenderer` + `IPdfRenderer` + `PdfRenderOptions` (neutral `Themia.Pdf`: `Render(string template, object model) -> string`, `RenderHtmlAsync(string html, PdfRenderOptions?, CancellationToken) -> Task<byte[]>`).
- Produces: `IPdfDocumentRenderer.RenderAsync(string key, object model, PdfRenderOptions? options = null, CancellationToken ct = default) -> Task<byte[]>` and `PdfDocumentRenderer` (internal). **Registered scoped** (Task 8).

- [ ] **Step 1: Write the failing test**

`PdfDocumentRendererTests.cs` (fakes for store + neutral interfaces):

```csharp
using Themia.Modules.Pdf.Rendering;
using Themia.Modules.Pdf.Store;
using Themia.Pdf;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfDocumentRendererTests
{
    [Fact]
    public async Task Resolves_then_merges_then_prints()
    {
        var store = new FakeStore(new PdfTemplate { Key = "invoice", Body = "TEMPLATE_BODY" });
        var html = new FakeHtml();
        var pdf = new FakePdf();
        var sut = new PdfDocumentRenderer(store, html, pdf);

        var bytes = await sut.RenderAsync("invoice", new { Total = 5 });

        Assert.Equal("TEMPLATE_BODY", html.LastTemplate);   // resolved body merged
        Assert.Equal(html.Returned, pdf.LastHtml);          // merged html printed
        Assert.Equal(pdf.Bytes, bytes);
    }

    private sealed class FakeStore(PdfTemplate t) : IPdfTemplateStore
    {
        public Task<PdfTemplate> ResolveAsync(string key, CancellationToken ct = default) => Task.FromResult(t);
        public Task<PdfTemplate> CreateAsync(PdfTemplate x, CancellationToken ct = default) => Task.FromResult(x);
        public Task<PdfTemplate> UpdateAsync(PdfTemplate x, CancellationToken ct = default) => Task.FromResult(x);
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PdfTemplate?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult<PdfTemplate?>(t);
        public Task<IReadOnlyList<PdfTemplate>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PdfTemplate>>([t]);
    }

    private sealed class FakeHtml : IHtmlTemplateRenderer
    {
        public string? LastTemplate { get; private set; }
        public string Returned => "MERGED_HTML";
        public string Render(string template, object model) { LastTemplate = template; return Returned; }
    }

    private sealed class FakePdf : IPdfRenderer
    {
        public string? LastHtml { get; private set; }
        public byte[] Bytes { get; } = [1, 2, 3];
        public Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default)
        { LastHtml = html; return Task.FromResult(Bytes); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Themia.sln --filter PdfDocumentRendererTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement**

`Rendering/IPdfDocumentRenderer.cs`:

```csharp
using Themia.Pdf;

namespace Themia.Modules.Pdf.Rendering;

/// <summary>Renders a stored template by key: resolves (tenant → global), merges the model, prints a PDF.</summary>
public interface IPdfDocumentRenderer
{
    /// <summary>Resolves the template for <paramref name="key"/>, merges it with <paramref name="model"/>,
    /// and prints the result to a PDF. Throws <see cref="TemplateNotFoundException"/> if unresolved.</summary>
    Task<byte[]> RenderAsync(string key, object model, PdfRenderOptions? options = null, CancellationToken cancellationToken = default);
}
```

`Rendering/PdfDocumentRenderer.cs`:

```csharp
using Themia.Modules.Pdf.Store;
using Themia.Pdf;

namespace Themia.Modules.Pdf.Rendering;

// Scoped: depends on the tenant-scoped IPdfTemplateStore while the neutral renderers are singletons.
internal sealed class PdfDocumentRenderer(
    IPdfTemplateStore store,
    IHtmlTemplateRenderer htmlRenderer,
    IPdfRenderer pdfRenderer) : IPdfDocumentRenderer
{
    public async Task<byte[]> RenderAsync(
        string key, object model, PdfRenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        var template = await store.ResolveAsync(key, cancellationToken).ConfigureAwait(false);
        var html = htmlRenderer.Render(template.Body, model);
        return await pdfRenderer.RenderHtmlAsync(html, options, cancellationToken).ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Themia.sln --filter PdfDocumentRendererTests`
Expected: PASS.

- [ ] **Step 5: Record PublicAPI + commit**

Add `IPdfDocumentRenderer` (+ `RenderAsync`) to `PublicAPI.Unshipped.txt`. Commit:

```bash
git add src/modules/Themia.Modules.Pdf tests/Themia.Modules.Pdf.Tests
git commit -m "feat: add render-by-key IPdfDocumentRenderer composing store and neutral renderer"
```

---

## Task 8: `PdfModuleOptions`, `PdfModule`, and the two DI entry points

**Files:**
- Create: `src/modules/Themia.Modules.Pdf/PdfModuleOptions.cs`
- Create: `src/modules/Themia.Modules.Pdf/PdfModule.cs`
- Create: `src/modules/Themia.Modules.Pdf/DependencyInjection/PdfModuleServiceCollectionExtensions.cs`
- Test: `tests/Themia.Modules.Pdf.Tests/PdfModuleRegistrationTests.cs`

**Interfaces:**
- Consumes: `EfPdfTemplateStore` (Task 4), `DapperPdfTemplateStore` (Task 5), `PdfDocumentRenderer` (Task 7), `PdfDbContext` (Task 3), `ThemiaModuleBase` + `ModuleDescriptor` + `MigrationEngine` + `ThemiaMigrations.Run` (framework), `IDatabaseProvider` + `DatabaseProviderNames` (framework), neutral `AddThemiaPdf`.
- Produces: `AddThemiaPdfModuleEfCore(this IServiceCollection, Action<PdfModuleOptions>?)` and `AddThemiaPdfModuleDapper(...)`; `PdfModule : ThemiaModuleBase`; `PdfModuleOptions` (with `string ConnectionStringName = "Default"`).

- [ ] **Step 1: Write the failing test**

`PdfModuleRegistrationTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Pdf.Rendering;
using Themia.Modules.Pdf.Store;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfModuleRegistrationTests
{
    [Fact]
    public void EfCore_entry_point_registers_store_and_renderer_as_scoped()
    {
        var services = new ServiceCollection();
        services.AddThemiaPdfModuleEfCore();

        Assert.Contains(services, d => d.ServiceType == typeof(IPdfTemplateStore) && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ServiceType == typeof(IPdfDocumentRenderer) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void Dapper_entry_point_registers_dapper_store()
    {
        var services = new ServiceCollection();
        services.AddThemiaPdfModuleDapper();
        Assert.Contains(services, d => d.ServiceType == typeof(IPdfTemplateStore)
            && d.ImplementationType == typeof(DapperPdfTemplateStore) && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void EfCore_entry_point_is_idempotent()
    {
        var services = new ServiceCollection();
        services.AddThemiaPdfModuleEfCore();
        services.AddThemiaPdfModuleEfCore();
        Assert.Single(services, d => d.ServiceType == typeof(IPdfDocumentRenderer));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Themia.sln --filter PdfModuleRegistrationTests`
Expected: FAIL — entry points do not exist.

- [ ] **Step 3: Implement options, module, and DI**

`PdfModuleOptions.cs`:

```csharp
namespace Themia.Modules.Pdf;

/// <summary>Configuration for the PDF module.</summary>
public sealed class PdfModuleOptions
{
    /// <summary>Name of the connection string (from configuration) the module migrates and connects to.</summary>
    public string ConnectionStringName { get; set; } = "Default";
}
```

`PdfModule.cs` (mirror `ExportModule` minus Quartz; runs the migration in `InitializeAsync`):

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Pdf.Migrations;

namespace Themia.Modules.Pdf;

/// <summary>Discoverable module that migrates the pdf_templates schema on startup.</summary>
public sealed class PdfModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly PdfModuleOptions options;

    public PdfModule(MigrationEngine engine) : this(engine, new PdfModuleOptions()) { }

    public PdfModule(MigrationEngine engine, PdfModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.engine = engine;
        this.options = options;
    }

    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Pdf",
        displayName: "PDF Templates",
        description: "Tenant-aware PDF/HTML template store with global-default fallback and render-by-key.",
        version: new Version(0, 7, 0, 0));

    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the PDF module requires it.");
        ThemiaMigrations.Run(engine, connectionString, typeof(PdfTemplateSchemaMigration).Assembly);
        return ValueTask.CompletedTask;
    }
}
```

`DependencyInjection/PdfModuleServiceCollectionExtensions.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Modules.Pdf.Rendering;
using Themia.Modules.Pdf.Store;

namespace Themia.Modules.Pdf.DependencyInjection;

public static class PdfModuleServiceCollectionExtensions
{
    /// <summary>Registers the PDF module on the EF Core data peer (SQL Server / PostgreSQL).</summary>
    public static IServiceCollection AddThemiaPdfModuleEfCore(
        this IServiceCollection services, Action<PdfModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddCommon(services, configure);

        services.AddDbContextFactory<PdfDbContext>((sp, db) =>
        {
            var provider = sp.GetRequiredService<IDatabaseProvider>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            var options = sp.GetRequiredService<IOptions<PdfModuleOptions>>().Value;
            var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{options.ConnectionStringName}' was not found; the PDF module requires it.");
            switch (provider.ProviderName)
            {
                case DatabaseProviderNames.Postgres: db.UseNpgsql(connectionString); break;
                case DatabaseProviderNames.SqlServer: db.UseSqlServer(connectionString); break;
                default:
                    throw new NotSupportedException(
                        $"Themia.Modules.Pdf on EF Core supports PostgreSQL and SQL Server; provider '{provider.ProviderName}' is not supported. Use the Dapper peer for MySQL.");
            }
            db.UseSnakeCaseNamingConvention();
        });
        services.TryAddScoped<PdfDbContext>(sp => sp.GetRequiredService<IDbContextFactory<PdfDbContext>>().CreateDbContext());
        services.TryAddScoped<IPdfTemplateStore, EfPdfTemplateStore>();
        return services;
    }

    /// <summary>Registers the PDF module on the Dapper data peer (SQL Server / PostgreSQL / MySQL).
    /// The app must also register the framework Dapper layer (AddThemiaDapperCore) and an engine provider.</summary>
    public static IServiceCollection AddThemiaPdfModuleDapper(
        this IServiceCollection services, Action<PdfModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddCommon(services, configure);
        services.TryAddScoped<IPdfTemplateStore, DapperPdfTemplateStore>();
        return services;
    }

    private static void AddCommon(IServiceCollection services, Action<PdfModuleOptions>? configure)
    {
        var optionsBuilder = services.AddOptions<PdfModuleOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }
        optionsBuilder
            .Validate(o => !string.IsNullOrWhiteSpace(o.ConnectionStringName),
                "PdfModuleOptions.ConnectionStringName must be set.")
            .ValidateOnStart();

        services.AddThemiaPdf();                                  // neutral renderers (singletons)
        services.TryAddSingleton<IDataFilterScope, DataFilterScope>();
        services.TryAddScoped<IPdfDocumentRenderer, PdfDocumentRenderer>();
    }
}
```

> Confirm `IDatabaseProvider` / `DatabaseProviderNames` live in `Themia.Framework.Data.EFCore.Abstractions` (that is the `using` in `ExportModuleServiceCollectionExtensions`). The Dapper store's write path needs `IRepository<PdfTemplate, Guid>` + `IUnitOfWork` + `ITenantQueryFactory` — all registered by the app's `AddThemiaDapperCore`; the module does not register them. Document this app-side requirement in the module's README / XML doc on `AddThemiaPdfModuleDapper`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Themia.sln --filter PdfModuleRegistrationTests`
Expected: PASS.

- [ ] **Step 5: Add the tenant-isolation (captive-dependency) regression test**

Append to `PdfModuleRegistrationTests.cs` a test that builds a provider with `ValidateScopes: true`, registers `AddThemiaPdfModuleEfCore` plus a fake `IPdfTemplateStore` that echoes the ambient tenant, resolves `IPdfDocumentRenderer` in two separate scopes with two different ambient tenants, and asserts each returns its own tenant's template — proving the scoped lifetime prevents first-tenant capture. (Use a fake store so the test needs no database.) Run:

Run: `dotnet test Themia.sln --filter PdfModuleRegistrationTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/modules/Themia.Modules.Pdf tests/Themia.Modules.Pdf.Tests
git commit -m "feat: add PdfModule and EfCore/Dapper DI entry points"
```

---

## Task 9: Version bump, CHANGELOG, PublicAPI shipped, solution wiring, full build/test

**Files:**
- Modify: `Directory.Build.props` (`<Version>`)
- Modify: `CHANGELOG.md`
- Modify: `src/framework/Themia.Framework.Data.Dapper/PublicAPI.Shipped.txt` (+ clear its Unshipped)
- Modify: `src/modules/Themia.Modules.Pdf/PublicAPI.Shipped.txt` (+ clear its Unshipped)
- Modify: `Themia.sln` (ensure the IntegrationTests project is added)

- [ ] **Step 1: Bump version**

In `Directory.Build.props`, bump `<Version>` to the next MINOR (new module) — `0.7.0` (confirm the current value first; if it already advanced past `0.6.9`, take the next unused `0.x.0`).

- [ ] **Step 2: CHANGELOG entries**

Under `## [Unreleased]` → a new version heading, add:

```markdown
### Added
- **`Themia.Framework.Data.Dapper`** — `ITenantQueryFactory.For<T>(bool includeGlobalRecords)` overload: a
  per-query override of the app-wide global-record inclusion, letting a caller opt one query into (or out of)
  the `tenant_id IS NULL` fallback without changing `DapperDataOptions`.
- **`Themia.Modules.Pdf`** — tenant-aware HTML/PDF template store (`net10.0`) with global-default fallback and a
  render-by-key service over the neutral `Themia.Pdf` core. EF Core peer (SQL Server, PostgreSQL) and Dapper peer
  (SQL Server, PostgreSQL, MySQL). One FluentMigrator schema owns the `pdf_templates` table for both peers.
```

- [ ] **Step 3: Promote PublicAPI Unshipped → Shipped**

For both `Themia.Framework.Data.Dapper` and `Themia.Modules.Pdf`: move every line from `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt` (keep `#nullable enable` at the top of each), leaving Unshipped with just `#nullable enable`.

- [ ] **Step 4: Full clean build + test**

Run: `dotnet build Themia.sln --no-incremental`
Expected: no `RS0016` (all public members documented + recorded).

Run: `dotnet test Themia.sln`
Expected: all unit tests PASS; integration tests PASS where Docker is available (the suite must not silently skip — document the Chromium + Testcontainers requirement in `tests/Themia.Modules.Pdf.IntegrationTests/README.md`).

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props CHANGELOG.md src/framework/Themia.Framework.Data.Dapper src/modules/Themia.Modules.Pdf Themia.sln
git commit -m "chore: bump version, changelog, and PublicAPI for Themia.Modules.Pdf (0.7.0)"
```

---

## Optional Task 10: end-to-end render integration (Chromium-gated)

**Files:**
- Test: `tests/Themia.Modules.Pdf.IntegrationTests/PdfRenderEndToEndTests.cs`

- [ ] **Step 1: Write the test** — store a template (`"<h1>{{Title}}</h1>"`), resolve+render via `IPdfDocumentRenderer.RenderAsync("doc", new { Title = "Hi" })`, assert the returned bytes begin with the `%PDF-` magic header and length > 0. Gate on Chromium being provisioned (mirror the neutral `Themia.Pdf.IntegrationTests` provisioning); document the requirement, do not silently skip.
- [ ] **Step 2: Run** `dotnet test Themia.sln --filter PdfRenderEndToEndTests` — Expected: PASS with Chromium available.
- [ ] **Step 3: Commit.**

---

## Self-Review

**Spec coverage:**
- EF + Dapper peers, 3 engines (MySQL via Dapper) → Tasks 4, 5, 6 + DI Task 8. ✓
- Framework prerequisite (`For<T>(bool)`) → Task 0. ✓
- Tenant→global resolution → resolver Task 2, EF Task 4, Dapper Task 5 (with the override). ✓
- Write asymmetry through UoW → Task 4 (EfUnitOfWork) + Task 5 (IUnitOfWork/DapperUnitOfWork) + tests. ✓
- `IPdfDocumentRenderer` scoped / captive-dependency guard → Task 7 + Task 8 Step 5. ✓
- Schema uniqueness (raw SQL per engine, no `NULLS NOT DISTINCT` dependency) → Task 6. ✓
- No concurrency token → `PdfTemplate` does not implement `IConcurrencyAware` (Task 1). ✓
- PublicAPI, version, CHANGELOG → Task 9. ✓
- Testing: unit (resolver, renderer, DI/lifetime) + integration (migration, both stores per engine incl. MySQL Dapper global fallback, write asymmetry, e2e render) → Tasks 2–8, 10. ✓

**Open items the implementer must resolve (flagged inline, not placeholders):**
- Entity id assignment without reflection → add `internal void AssignId(Guid)` to `PdfTemplate` (Task 4 note); update both stores to use it.
- `TenantId` Dapper type-handler + `required`-member materialization → verify against framework Dapper tests (Task 5 note).
- MySQL functional-index support for the uniqueness rule → confirm the target MySQL version (Task 6 note).
- Integration fixtures (`PdfPostgresFixture` / `PdfMySqlFixture` / SqlServer) → build by mirroring the Export/Notifications integration fixtures.

**Type consistency:** `IPdfTemplateStore` signatures, `IPdfDocumentRenderer.RenderAsync`, `ITenantQueryFactory.For<T>(bool)`, and `PdfTemplateResolver.PreferTenant` are used identically across Tasks 2–8. ✓
