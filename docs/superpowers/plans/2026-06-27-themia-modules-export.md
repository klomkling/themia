# Themia.Modules.Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a tenant-aware `Themia.Modules.Export` that runs the shipped neutral export writers off the request thread — on demand or on a cron schedule — persists the file to Storage, and notifies the requesting user when ready/failed; plus the data-layer `BypassSoftDeleteFilter()` primitive the module needs to optionally include soft-deleted rows.

**Architecture:** A new module composes three existing modules (Storage, Scheduling/Quartz, Notifications) over the neutral `Themia.Export(.Excel)` writers. App authors register keyed `IExportDefinition`s; a persisted `export_runs`/`export_schedules` store (EF entities, FluentMigrator DDL) tracks state. Quartz runs a one-shot `ExportJob` for on-demand, a cron trigger for recurring, and a recurring `CleanupJob` for retention. A new `IDataFilterScope.BypassSoftDeleteFilter()` (EF + Dapper) lets a gated definition read soft-deleted business rows without dropping tenant isolation.

**Tech Stack:** .NET 10 (`net10.0` module), C# 12, EF Core 10, FluentMigrator 8, Quartz 3.15, ClosedXML (via the neutral writer), xUnit + Testcontainers. Central Package Management, PublicAPI analyzers.

**Spec:** `docs/superpowers/specs/2026-06-27-themia-modules-export-design.md`

## Global Constraints

- **Target frameworks:** the module is `net10.0` (module policy). The framework changes (Tasks 1–3) live in existing `net8.0;net10.0` / `net10.0` framework projects — do not change their TFMs. Test projects are `net10.0`.
- **`Directory.Build.props` sets globally — never repeat in a csproj:** `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true`, shared `<Version>`. A clean build reports undocumented public members as `RS0016`; every public type/member needs an XML doc comment + a `PublicAPI.Unshipped.txt` entry.
- **CPM:** versions live in `Directory.Packages.props` (already pins Quartz 3.15.2, FluentMigrator 8.0.1 + runners, EF Core 10.0.9, `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.9, xunit 2.8.1). csproj `<PackageReference>` carries **no** `Version`.
- **System.Text.Json only — never Newtonsoft.** Log via `ILogger<T>` only. Log once per handled exception (no double-logging in catch clauses).
- **DI extensions** use `Microsoft.Extensions.DependencyInjection.Extensions` `TryAdd*` and guard args with `ArgumentNullException.ThrowIfNull`.
- **Schema/DDL is owned by FluentMigrator** (one migration, `IfDatabase(...)` per engine) — **no `dotnet ef migrations add`.**
- **Tenant isolation is non-negotiable:** every background code path must establish tenant context (Task 7) before any tenant-scoped query/write; the soft-delete bypass must never reveal another tenant's rows.

---

## File Structure

**Framework changes (existing projects):**
- `src/framework/Themia.Framework.Data.Abstractions/Filtering/IDataFilterScope.cs` (modify) — add `BypassSoftDeleteFilter()` + `IsSoftDeleteFilterBypassed`.
- `src/framework/Themia.Framework.Data.Abstractions/Filtering/DataFilterScope.cs` (modify) — second `AsyncLocal<bool>` + static ambient accessor.
- `src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs` (modify) — make the soft-delete clause read the ambient flag (instance member referenced through `this`).
- `src/framework/Themia.Framework.Data.Dapper/Tenancy/TenantPredicate.cs` (modify) — omit `IsDeleted=false` under the flag.

**New module `src/modules/Themia.Modules.Export/`** (`net10.0`):
- `Themia.Modules.Export.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`, `AssemblyInfo.cs`
- `ExportFormat.cs`, `ExportRunStatus.cs` (enums)
- `Definitions/IExportDefinition.cs`, `Definitions/ExportContext.cs`, `Definitions/ExportDefinition.cs` (typed bases), `Definitions/ExportDefinitionRegistry.cs`
- `Entities/ExportRun.cs`, `Entities/ExportSchedule.cs`
- `EntityConfiguration/ExportRunConfiguration.cs`, `EntityConfiguration/ExportScheduleConfiguration.cs`
- `ExportDbContext.cs`
- `Migrations/ExportSchemaMigration.cs`
- `Store/IExportRunStore.cs`, `Store/ExportRunStore.cs`
- `Jobs/BackgroundTenantScope.cs`, `Jobs/ExportJob.cs`, `Jobs/CleanupJob.cs`
- `Requests/IExportRequestService.cs`, `Requests/ExportRequestService.cs`, `Requests/ExportSubmission.cs`, `Requests/ExportScheduleRequest.cs`, `Requests/ExportRunView.cs`
- `ExportModuleOptions.cs`, `ExportModule.cs`, `DependencyInjection/ExportModuleServiceCollectionExtensions.cs`

**Tests:**
- `tests/Themia.Framework.Data.EFCore.IntegrationTests/` (add a soft-delete-bypass fact)
- `tests/Themia.Framework.Data.Dapper.Conformance/` (add a soft-delete-bypass fact)
- `tests/Themia.Modules.Export.Tests/` (contract + store + job + service, Testcontainers for DB)

---

## Task 1: Data-layer soft-delete bypass primitive

**Files:**
- Modify: `src/framework/Themia.Framework.Data.Abstractions/Filtering/IDataFilterScope.cs`
- Modify: `src/framework/Themia.Framework.Data.Abstractions/Filtering/DataFilterScope.cs`
- Modify: `src/framework/Themia.Framework.Data.Abstractions/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Framework.Data.Abstractions.Tests/DataFilterScopeTests.cs` (create; if no such test project exists, add the fact to the nearest existing abstractions/unit test project — verify with `dotnet sln list`)

**Interfaces:**
- Produces: `IDataFilterScope.BypassSoftDeleteFilter() -> IDisposable`, `IDataFilterScope.IsSoftDeleteFilterBypassed -> bool`, and `static bool DataFilterScope.SoftDeleteBypassedAmbient { get; }` (read by the EF expression in Task 2).

- [ ] **Step 1: Write the failing test** `DataFilterScopeTests.cs`:

```csharp
using Themia.Framework.Data.Abstractions.Filtering;
using Xunit;

namespace Themia.Framework.Data.Abstractions.Tests;

public sealed class DataFilterScopeTests
{
    [Fact]
    public void BypassSoftDeleteFilter_is_scoped_and_independent_of_tenant_bypass()
    {
        var scope = new DataFilterScope();
        Assert.False(scope.IsSoftDeleteFilterBypassed);
        Assert.False(DataFilterScope.SoftDeleteBypassedAmbient);

        using (scope.BypassSoftDeleteFilter())
        {
            Assert.True(scope.IsSoftDeleteFilterBypassed);
            Assert.True(DataFilterScope.SoftDeleteBypassedAmbient);
            Assert.False(scope.IsTenantFilterBypassed); // axes are independent
        }

        Assert.False(scope.IsSoftDeleteFilterBypassed);
        Assert.False(DataFilterScope.SoftDeleteBypassedAmbient);
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/Themia.Framework.Data.Abstractions.Tests --filter DataFilterScopeTests`
Expected: FAIL — `BypassSoftDeleteFilter` / `SoftDeleteBypassedAmbient` do not exist.

- [ ] **Step 3: Extend `IDataFilterScope.cs`** — add the two members after the existing tenant ones:

```csharp
    /// <summary>Bypasses ONLY the soft-delete predicate until the returned scope is disposed.
    /// Tenant isolation and audit remain enforced — this never reveals another tenant's rows.</summary>
    IDisposable BypassSoftDeleteFilter();

    /// <summary>True while a soft-delete bypass scope is active on the current async flow.</summary>
    bool IsSoftDeleteFilterBypassed { get; }
```

- [ ] **Step 4: Extend `DataFilterScope.cs`** to its full form:

```csharp
namespace Themia.Framework.Data.Abstractions.Filtering;

/// <summary>AsyncLocal-backed <see cref="IDataFilterScope"/>. The single shared filter-bypass carrier
/// for both the tenant and soft-delete axes.</summary>
public sealed class DataFilterScope : IDataFilterScope
{
    private static readonly AsyncLocal<bool> TenantBypassed = new();
    private static readonly AsyncLocal<bool> SoftDeleteBypassed = new();

    /// <inheritdoc />
    public bool IsTenantFilterBypassed => TenantBypassed.Value;

    /// <inheritdoc />
    public bool IsSoftDeleteFilterBypassed => SoftDeleteBypassed.Value;

    /// <summary>The ambient soft-delete bypass flag, read by the EF query filter expression at query time.</summary>
    public static bool SoftDeleteBypassedAmbient => SoftDeleteBypassed.Value;

    /// <inheritdoc />
    public IDisposable BypassTenantFilter()
    {
        var previous = TenantBypassed.Value;
        TenantBypassed.Value = true;
        return new Restore(() => TenantBypassed.Value = previous);
    }

    /// <inheritdoc />
    public IDisposable BypassSoftDeleteFilter()
    {
        var previous = SoftDeleteBypassed.Value;
        SoftDeleteBypassed.Value = true;
        return new Restore(() => SoftDeleteBypassed.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
```

(Note: the existing single `Bypassed` field is renamed to `TenantBypassed` — update no other code; `IsTenantFilterBypassed` keeps its contract.)

- [ ] **Step 5: Run the test.**

Run: `dotnet test tests/Themia.Framework.Data.Abstractions.Tests --filter DataFilterScopeTests`
Expected: PASS.

- [ ] **Step 6: Update `PublicAPI.Unshipped.txt`** for the Abstractions project — add:

```text
Themia.Framework.Data.Abstractions.Filtering.IDataFilterScope.BypassSoftDeleteFilter() -> System.IDisposable!
Themia.Framework.Data.Abstractions.Filtering.IDataFilterScope.IsSoftDeleteFilterBypassed.get -> bool
Themia.Framework.Data.Abstractions.Filtering.DataFilterScope.BypassSoftDeleteFilter() -> System.IDisposable!
Themia.Framework.Data.Abstractions.Filtering.DataFilterScope.IsSoftDeleteFilterBypassed.get -> bool
static Themia.Framework.Data.Abstractions.Filtering.DataFilterScope.SoftDeleteBypassedAmbient.get -> bool
```

- [ ] **Step 7: Build the abstractions project clean.**

Run: `dotnet build src/framework/Themia.Framework.Data.Abstractions --no-incremental`
Expected: `0 Warning(s) 0 Error(s)` (fix any `RS0016` by copying the reported symbol into `PublicAPI.Unshipped.txt`).

- [ ] **Step 8: Commit.**

```bash
git add src/framework/Themia.Framework.Data.Abstractions tests/Themia.Framework.Data.Abstractions.Tests
git commit -m "feat: add BypassSoftDeleteFilter to IDataFilterScope (data-layer primitive)"
```

---

## Task 2: EF adapter honors the soft-delete bypass

**Files:**
- Modify: `src/framework/Themia.Framework.Data.EFCore/ThemiaDbContext.cs` (`ApplyTenantQueryFilters` ~560-591, `ApplySoftDeleteQueryFilters` ~593-615, add an instance member)
- Test: `tests/Themia.Framework.Data.EFCore.IntegrationTests/Database/SoftDeleteBypassTests.cs` (create; reuse the existing `PostgresFixture` pattern in that project)

**Interfaces:**
- Consumes: `DataFilterScope.SoftDeleteBypassedAmbient` (Task 1).
- Produces: under a `BypassSoftDeleteFilter()` scope, EF queries on `ITenantEntity`+`ISoftDeletable` and on plain `ISoftDeletable` entities return soft-deleted rows too, while the tenant predicate still applies.

**Why an instance member:** EF re-evaluates query-filter sub-expressions referenced through the captured `this` constant on every query (that is how the existing tenant filter stays dynamic — `GetCurrentTenantExpression` references `this.AmbientFilterTenantId`). A bare `static` property may be baked once at model build. So expose the ambient flag as an **instance** property and reference it via the existing `this` constant.

- [ ] **Step 1: Write the failing test** `SoftDeleteBypassTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Xunit;

namespace Themia.Framework.Data.EFCore.IntegrationTests.Database;

public sealed class SoftDeleteBypassTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task BypassSoftDeleteFilter_includes_deleted_but_keeps_tenant_isolation()
    {
        await fixture.ResetDataAsync();
        var scope = new DataFilterScope();

        // Tenant A: one live + one soft-deleted product.
        await using (var ctx = fixture.CreateContext(new TenantId("a")))
        {
            ctx.Products.Add(new MigrationProduct { Id = Guid.NewGuid(), Name = "live", Sku = "L1", TenantId = new TenantId("a") });
            var gone = new MigrationProduct { Id = Guid.NewGuid(), Name = "gone", Sku = "G1", TenantId = new TenantId("a"), IsDeleted = true };
            ctx.Products.Add(gone);
            await ctx.SaveChangesAsync();
        }
        // Tenant B: one live product that must stay invisible to A.
        await using (var ctx = fixture.CreateContext(new TenantId("b")))
        {
            ctx.Products.Add(new MigrationProduct { Id = Guid.NewGuid(), Name = "b-live", Sku = "B1", TenantId = new TenantId("b") });
            await ctx.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext(new TenantId("a"), scope);
        using (scope.BypassSoftDeleteFilter())
        {
            var rows = await read.Products.ToListAsync();
            Assert.Equal(2, rows.Count);                       // A's live + A's soft-deleted
            Assert.All(rows, p => Assert.Equal(new TenantId("a"), p.TenantId)); // never B's row
        }
    }
}
```

If `PostgresFixture.CreateContext` / `MigrationProduct` lacks `IsDeleted` or a `DataFilterScope` overload, extend the fixture's `TestMigrationDbContext` to make `MigrationProduct : SoftDeletableEntity<Guid>` and add a `CreateContext(TenantId?, IDataFilterScope?)` overload that passes the scope into the context (the context must receive an `IDataFilterScope`; if it currently does not, inject `new DataFilterScope()` by default). Keep these test-infra edits inside this task.

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/Themia.Framework.Data.EFCore.IntegrationTests --filter SoftDeleteBypassTests`
Expected: FAIL — the soft-deleted row is hidden (count 1, not 2).

- [ ] **Step 3: Add an instance member to `ThemiaDbContext`** (near `EffectiveFilterTenantId`):

```csharp
    /// <summary>The ambient soft-delete bypass flag, referenced by the soft-delete query filter through
    /// the captured <c>this</c> so EF re-evaluates it per query (mirrors the tenant filter's dynamism).</summary>
    private bool SoftDeleteFilterBypassed => DataFilterScope.SoftDeleteBypassedAmbient;

    private static readonly System.Reflection.PropertyInfo SoftDeleteBypassProperty =
        typeof(ThemiaDbContext).GetProperty(nameof(SoftDeleteFilterBypassed),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
```

- [ ] **Step 4: Make the soft-delete clause conditional in `ApplyTenantQueryFilters`** — replace the combined-filter block (the `notDeleted` constant AND) with `bypass || !IsDeleted`:

```csharp
        // Combine with soft delete filter if entity supports it.
        Expression finalPredicate = tenantPredicate;
        if (EnableSoftDeleteFilters && typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
        {
            var isDeletedProperty = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
            var notDeleted = Expression.Equal(isDeletedProperty, Expression.Constant(false));
            // bypass flag read through `this` so EF re-evaluates per query.
            var bypass = Expression.Property(
                Expression.Constant(this, typeof(ThemiaDbContext)), SoftDeleteBypassProperty);
            var softDeleteClause = Expression.OrElse(bypass, notDeleted);
            finalPredicate = Expression.AndAlso(tenantPredicate, softDeleteClause);
        }
```

- [ ] **Step 5: Make the standalone (non-tenant) soft-delete filter conditional in `ApplySoftDeleteQueryFilters`** — replace its `notDeleted` lambda:

```csharp
            var parameter = Expression.Parameter(entityType.ClrType, "entity");
            var isDeletedProperty = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
            var notDeleted = Expression.Equal(isDeletedProperty, Expression.Constant(false));
            var bypass = Expression.Property(
                Expression.Constant(this, typeof(ThemiaDbContext)), SoftDeleteBypassProperty);

            var filter = Expression.Lambda(Expression.OrElse(bypass, notDeleted), parameter);
            entityType.SetQueryFilter(filter);
```

- [ ] **Step 6: Run the test.**

Run: `dotnet test tests/Themia.Framework.Data.EFCore.IntegrationTests --filter SoftDeleteBypassTests`
Expected: PASS (count 2, all tenant A).

- [ ] **Step 7: Regression — run the existing EF tenant/soft-delete suite.**

Run: `dotnet test tests/Themia.Framework.Data.EFCore.IntegrationTests`
Expected: PASS (the existing tenant-bypass tests still hide soft-deleted rows — they don't open a soft-delete scope, so `bypass` is `false`).

- [ ] **Step 8: Commit.**

```bash
git add src/framework/Themia.Framework.Data.EFCore tests/Themia.Framework.Data.EFCore.IntegrationTests
git commit -m "feat: EF query filters honor BypassSoftDeleteFilter (tenant isolation preserved)"
```

---

## Task 3: Dapper adapter honors the soft-delete bypass

**Files:**
- Modify: `src/framework/Themia.Framework.Data.Dapper/Tenancy/TenantPredicate.cs`
- Modify: `src/framework/Themia.Framework.Data.Dapper/Tenancy/TenantQueryFactory.cs` (pass the flag)
- Test: `tests/Themia.Framework.Data.Dapper.Conformance/DataLayerConformanceTests.cs` (add a fact mirroring the existing `BypassTenantFilter_StillHidesSoftDeleted`)

**Interfaces:**
- Consumes: `IDataFilterScope.IsSoftDeleteFilterBypassed` (Task 1).
- Produces: `ITenantQueryFactory.For<T>()` omits the `IsDeleted = false` clause when a soft-delete bypass scope is active; the tenant predicate is unaffected.

- [ ] **Step 1: Write the failing test** — add to `DataLayerConformanceTests.cs`:

```csharp
[Fact]
public async Task BypassSoftDeleteFilter_RevealsOwnSoftDeleted_SameTenant()
{
    await ResetAsync();

    Guid id;
    await using (var a = await NewScopeAsync(new TenantId("a")))
    {
        var w = NewWidget("sd", 1);
        id = w.Id;
        await a.Repo.AddAsync(w);
        await a.Uow.SaveChangesAsync();
        a.Repo.Remove(w);
        await a.Uow.SaveChangesAsync();
    }

    await using var a2 = await NewScopeAsync(new TenantId("a"));
    using (a2.Filter.BypassSoftDeleteFilter())
    {
        var visible = await a2.Repo.ListAsync(new WidgetByNameSpec("sd"));
        Assert.Single(visible); // soft-deleted row now visible to its own tenant
    }
}
```

(If the conformance harness's repository reads through `ITenantQueryFactory`, this exercises the Dapper path directly; if the harness covers both providers, this fact runs against both — confirm by the existing facts' base class.)

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.Conformance --filter BypassSoftDeleteFilter_RevealsOwnSoftDeleted_SameTenant`
Expected: FAIL — soft-deleted row still hidden.

- [ ] **Step 3: Add a `bypassSoftDelete` parameter to `TenantPredicate.Apply`:**

```csharp
    public static void Apply<T>(
        Query query,
        TenantId? tenant,
        bool includeGlobalRecords,
        bool bypassTenantFilter,
        bool bypassSoftDeleteFilter,
        EntityMapping map)
    {
        if (typeof(ITenantEntity).IsAssignableFrom(typeof(T)) && !bypassTenantFilter)
        {
            var column = map.Column(nameof(ITenantEntity.TenantId));
            if (tenant is { } t)
            {
                if (includeGlobalRecords)
                    query.Where(q => q.Where(column, t.Value).OrWhereNull(column));
                else
                    query.Where(column, t.Value);
            }
            else
            {
                query.WhereNull(column);
            }
        }

        if (typeof(ISoftDeletable).IsAssignableFrom(typeof(T)) && !bypassSoftDeleteFilter)
            query.Where(map.Column(nameof(ISoftDeletable.IsDeleted)), false);
    }
```

- [ ] **Step 4: Pass the flag from `TenantQueryFactory.For<T>`:**

```csharp
        TenantPredicate.Apply<T>(
            query,
            tenantContext.CurrentTenantId,
            options.IncludeGlobalRecordsForTenants,
            filterScope.IsTenantFilterBypassed,
            filterScope.IsSoftDeleteFilterBypassed,
            map);
```

Grep for any other `TenantPredicate.Apply` call site and pass `false` for the new parameter (default behavior).

- [ ] **Step 5: Run the test + the Dapper suite.**

Run: `dotnet test tests/Themia.Framework.Data.Dapper.Conformance`
Expected: PASS — new fact green; existing `BypassTenantFilter_StillHidesSoftDeleted` still green (no soft-delete scope there).

- [ ] **Step 6: Commit.**

```bash
git add src/framework/Themia.Framework.Data.Dapper tests/Themia.Framework.Data.Dapper.Conformance
git commit -m "feat: Dapper tenant query factory honors BypassSoftDeleteFilter"
```

---

## Task 4: Module skeleton + contract types

**Files:**
- Create: `src/modules/Themia.Modules.Export/Themia.Modules.Export.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`, `AssemblyInfo.cs`
- Create: `ExportFormat.cs`, `ExportRunStatus.cs`, `Definitions/ExportContext.cs`, `Definitions/IExportDefinition.cs`, `Definitions/ExportDefinition.cs`
- Create: `tests/Themia.Modules.Export.Tests/Themia.Modules.Export.Tests.csproj`, `tests/Themia.Modules.Export.Tests/ExportDefinitionTests.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Produces:
  - `enum ExportFormat { Csv, Xlsx }`, `enum ExportRunStatus { Pending, Running, Succeeded, Failed, Expired }`
  - `sealed class ExportContext { TenantId? TenantId; string? UserId; string? ParametersJson; ExportFormat Format; string? FileName; bool IncludeSoftDeleted }`
  - `interface IExportDefinition { string Key; bool AllowsIncludeSoftDeleted; Task<ExportResult> ExportAsync(ExportContext, CancellationToken) }`
  - `abstract class ExportDefinition<TRow, TParams> : IExportDefinition where TParams : new()` and `abstract class ExportDefinition<TRow> : ExportDefinition<TRow, EmptyParams>`
- Consumes: `Themia.Export.ExportColumn<T>`, `ReportHeader`, `ExportResult`, `ICsvExporter`, `IExcelExporter`.

- [ ] **Step 1: Create `Themia.Modules.Export.csproj`:**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Export</PackageId>
    <Description>Tenant-aware asynchronous and scheduled export: keyed export definitions, Quartz-run jobs, file persisted to Storage, completion notification. Composes Themia.Export(.Excel), Storage, Scheduling, Notifications.</Description>
    <PackageTags>themia;export;module;scheduling;async;report</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../framework/Themia.Framework.Core/Themia.Framework.Core.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
    <ProjectReference Include="../../neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
    <ProjectReference Include="../../neutral/Themia.Export/Themia.Export.csproj" />
    <ProjectReference Include="../../neutral/Themia.Export.Excel/Themia.Export.Excel.csproj" />
    <ProjectReference Include="../Themia.Modules.Storage/Themia.Modules.Storage.csproj" />
    <ProjectReference Include="../Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj" />
    <ProjectReference Include="../Themia.Modules.Notifications/Themia.Modules.Notifications.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Options" />
    <PackageReference Include="Quartz" />
    <PackageReference Include="EFCore.NamingConventions" />
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
    <InternalsVisibleTo Include="Themia.Modules.Export.Tests" />
  </ItemGroup>
</Project>
```

Verify each referenced csproj path against the exact storage/scheduling/notifications module csproj names (the second exploration confirmed those three modules and `Themia.Data.Migrations` exist). `PublicAPI.Shipped.txt` = one line `#nullable enable`.

- [ ] **Step 2: Create the enums.**

`ExportFormat.cs`:
```csharp
namespace Themia.Modules.Export;

/// <summary>The output format an export produces.</summary>
public enum ExportFormat
{
    /// <summary>CSV via <c>Themia.Export</c>.</summary>
    Csv,
    /// <summary>Excel <c>.xlsx</c> via <c>Themia.Export.Excel</c>.</summary>
    Xlsx,
}
```

`ExportRunStatus.cs`:
```csharp
namespace Themia.Modules.Export;

/// <summary>Lifecycle state of an export run.</summary>
public enum ExportRunStatus
{
    /// <summary>Queued, not yet executing.</summary>
    Pending,
    /// <summary>Executing.</summary>
    Running,
    /// <summary>Produced and stored; bytes available until <c>ExpiresAt</c>.</summary>
    Succeeded,
    /// <summary>Terminated by an error.</summary>
    Failed,
    /// <summary>Retention elapsed; bytes purged, record kept as history.</summary>
    Expired,
}
```

- [ ] **Step 3: Create `Definitions/ExportContext.cs`:**

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Definitions;

/// <summary>The ambient inputs a definition runs against. Built by the job from the persisted run/schedule.</summary>
public sealed class ExportContext
{
    /// <summary>The tenant the export runs for.</summary>
    public required TenantId? TenantId { get; init; }
    /// <summary>The user who requested it (notification target); may be null for system schedules.</summary>
    public string? UserId { get; init; }
    /// <summary>The raw filter/scope parameters (System.Text.Json); deserialized by the typed base.</summary>
    public string? ParametersJson { get; init; }
    /// <summary>The requested output format.</summary>
    public required ExportFormat Format { get; init; }
    /// <summary>An optional download file name (without the job's timestamp default).</summary>
    public string? FileName { get; init; }
    /// <summary>Whether soft-deleted business rows should be included (gated by the definition).</summary>
    public bool IncludeSoftDeleted { get; init; }
}
```

- [ ] **Step 4: Create `Definitions/IExportDefinition.cs`:**

```csharp
using Themia.Export;

namespace Themia.Modules.Export.Definitions;

/// <summary>A keyed, app-registered export. The persisted job stores only the key + parameters + format;
/// the definition reconstructs rows and columns at run time. No delegates are ever serialized.</summary>
public interface IExportDefinition
{
    /// <summary>The stable lookup key, e.g. <c>"sales-report"</c>.</summary>
    string Key { get; }

    /// <summary>Whether this definition permits the <c>IncludeSoftDeleted</c> opt-in. Default false.</summary>
    bool AllowsIncludeSoftDeleted { get; }

    /// <summary>Produces the file for the given context.</summary>
    /// <exception cref="InvalidOperationException">Parameters are invalid, or a non-numeric value hits an aggregate.</exception>
    Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken);
}
```

- [ ] **Step 5: Create `Definitions/ExportDefinition.cs`** (typed base + params-free base):

```csharp
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Themia.Export;
using Themia.Export.Csv;
using Themia.Export.Excel;

namespace Themia.Modules.Export.Definitions;

/// <summary>Empty parameter type for definitions that take no filter.</summary>
public sealed class EmptyParams;

/// <summary>Convenience base: typed rows + typed, validated filter params. Deserializes
/// <see cref="ExportContext.ParametersJson"/> to <typeparamref name="TParams"/>, validates it, then
/// dispatches to the CSV or Excel writer per <see cref="ExportContext.Format"/>.</summary>
/// <typeparam name="TRow">The row type.</typeparam>
/// <typeparam name="TParams">The strongly-typed filter/scope parameters.</typeparam>
public abstract class ExportDefinition<TRow, TParams> : IExportDefinition
    where TParams : new()
{
    private readonly ICsvExporter csv;
    private readonly IExcelExporter excel;

    /// <summary>Creates the base with the injected neutral writers.</summary>
    protected ExportDefinition(ICsvExporter csv, IExcelExporter excel)
    {
        this.csv = csv ?? throw new ArgumentNullException(nameof(csv));
        this.excel = excel ?? throw new ArgumentNullException(nameof(excel));
    }

    /// <inheritdoc />
    public abstract string Key { get; }

    /// <inheritdoc />
    public virtual bool AllowsIncludeSoftDeleted => false;

    /// <summary>The columns for the given params/context.</summary>
    protected abstract IReadOnlyList<ExportColumn<TRow>> Columns(TParams parameters, ExportContext context);

    /// <summary>The rows for the given params/context. Apply filters/scope here.</summary>
    protected abstract Task<IReadOnlyList<TRow>> RowsAsync(TParams parameters, ExportContext context, CancellationToken ct);

    /// <summary>Optional report-header lines above the table.</summary>
    protected virtual IEnumerable<ReportHeader> Headers(TParams parameters, ExportContext context) => [];

    /// <inheritdoc />
    public async Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var parameters = Deserialize(context.ParametersJson);
        var rows = await RowsAsync(parameters, context, cancellationToken).ConfigureAwait(false);
        var columns = Columns(parameters, context);
        var headers = Headers(parameters, context);

        return context.Format switch
        {
            ExportFormat.Csv => csv.Export(rows, columns, headers, context.FileName),
            ExportFormat.Xlsx => excel.Export(rows, columns, options: null, headers, context.FileName),
            _ => throw new InvalidOperationException($"Unsupported export format '{context.Format}'."),
        };
    }

    private static TParams Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TParams();
        }

        TParams value;
        try
        {
            value = JsonSerializer.Deserialize<TParams>(json) ?? new TParams();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Export parameters are not valid JSON.", ex);
        }

        Validator.ValidateObject(value, new ValidationContext(value), validateAllProperties: true);
        return value;
    }
}

/// <summary>Convenience base for a definition that takes no filter parameters.</summary>
/// <typeparam name="TRow">The row type.</typeparam>
public abstract class ExportDefinition<TRow> : ExportDefinition<TRow, EmptyParams>
{
    /// <summary>Creates the base with the injected neutral writers.</summary>
    protected ExportDefinition(ICsvExporter csv, IExcelExporter excel) : base(csv, excel) { }

    /// <summary>The columns (no parameters).</summary>
    protected abstract IReadOnlyList<ExportColumn<TRow>> Columns(ExportContext context);

    /// <summary>The rows (no parameters).</summary>
    protected abstract Task<IReadOnlyList<TRow>> RowsAsync(ExportContext context, CancellationToken ct);

    /// <inheritdoc />
    protected sealed override IReadOnlyList<ExportColumn<TRow>> Columns(EmptyParams parameters, ExportContext context) => Columns(context);

    /// <inheritdoc />
    protected sealed override Task<IReadOnlyList<TRow>> RowsAsync(EmptyParams parameters, ExportContext context, CancellationToken ct) => RowsAsync(context, ct);
}
```

- [ ] **Step 6: Write the contract test** `tests/Themia.Modules.Export.Tests/ExportDefinitionTests.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Themia.Export;
using Themia.Export.Csv;
using Themia.Export.Excel;
using Themia.Modules.Export;
using Themia.Modules.Export.Definitions;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportDefinitionTests
{
    private sealed record Sale(string Product, decimal Amount);
    private sealed class SaleParams { [Range(0, int.MaxValue)] public int MinAmount { get; set; } }

    private sealed class SaleDef(ICsvExporter csv, IExcelExporter excel) : ExportDefinition<Sale, SaleParams>(csv, excel)
    {
        public override string Key => "sales";
        protected override IReadOnlyList<ExportColumn<Sale>> Columns(SaleParams p, ExportContext c) =>
            [new() { Title = "Product", Value = s => s.Product }, new() { Title = "Amount", Value = s => s.Amount, Aggregate = AggregateKind.Sum }];
        protected override Task<IReadOnlyList<Sale>> RowsAsync(SaleParams p, ExportContext c, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Sale>>([new("Apple", 10m), new("Pear", 5m)]);
    }

    private static SaleDef NewDef() => new(new CsvExporter(), new ExcelExporter());

    [Fact]
    public async Task Csv_format_produces_csv_bytes_with_summary()
    {
        var result = await NewDef().ExportAsync(new ExportContext { TenantId = null, Format = ExportFormat.Csv }, default);
        Assert.Equal("text/csv", result.ContentType);
        Assert.Contains("15", System.Text.Encoding.UTF8.GetString(result.Content));
    }

    [Fact]
    public async Task Invalid_params_throw_InvalidOperationException()
    {
        var ctx = new ExportContext { TenantId = null, Format = ExportFormat.Csv, ParametersJson = "{\"MinAmount\":-1}" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewDef().ExportAsync(ctx, default));
    }
}
```

The test project csproj (`net10.0`, `IsPackable=false`, `GenerateDocumentationFile=false`) references `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, and `../../src/modules/Themia.Modules.Export/Themia.Modules.Export.csproj`.

- [ ] **Step 7: Add projects to the solution, populate `PublicAPI.Unshipped.txt`, build, test.**

```bash
dotnet sln Themia.sln add src/modules/Themia.Modules.Export/Themia.Modules.Export.csproj
dotnet sln Themia.sln add tests/Themia.Modules.Export.Tests/Themia.Modules.Export.Tests.csproj
dotnet build src/modules/Themia.Modules.Export/Themia.Modules.Export.csproj --no-incremental
dotnet test tests/Themia.Modules.Export.Tests --filter ExportDefinitionTests
```
Expected: build `0 Warning(s)` (fix `RS0016` by copying each reported public symbol into `PublicAPI.Unshipped.txt` — enums, `ExportContext`, `IExportDefinition`, both `ExportDefinition<>` bases, `EmptyParams`); tests PASS.

- [ ] **Step 8: Commit.**

```bash
git add src/modules/Themia.Modules.Export tests/Themia.Modules.Export.Tests Themia.sln
git commit -m "feat: Themia.Modules.Export skeleton + export definition contract"
```

---

## Task 5: Entities, EF configuration, DbContext, FluentMigrator schema

**Files:**
- Create: `Entities/ExportRun.cs`, `Entities/ExportSchedule.cs`
- Create: `EntityConfiguration/ExportRunConfiguration.cs`, `EntityConfiguration/ExportScheduleConfiguration.cs`
- Create: `ExportDbContext.cs`
- Create: `Migrations/ExportSchemaMigration.cs`
- Test: `tests/Themia.Modules.Export.Tests/ExportSchemaMigrationTests.cs` (Testcontainers — PostgreSQL, mirroring the EFCore IntegrationTests fixture)

**Interfaces:**
- Produces: `ExportRun : SoftDeletableEntity<Guid>, ITenantEntity` and `ExportSchedule : SoftDeletableEntity<Guid>, ITenantEntity` with the columns from the spec; `ExportDbContext` exposing `DbSet<ExportRun> Runs` / `DbSet<ExportSchedule> Schedules`; `ExportSchemaMigration` creating `export.export_runs` + `export.export_schedules` on all three engines.

- [ ] **Step 1: Create `Entities/ExportRun.cs`:**

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Entities;

/// <summary>Persisted state of a single export run (history + retention).</summary>
public sealed class ExportRun : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }
    /// <summary>The requesting user (notification target); null for system schedules.</summary>
    public string? UserId { get; set; }
    /// <summary>The export definition key.</summary>
    public string DefinitionKey { get; set; } = string.Empty;
    /// <summary>The filter/scope parameters (System.Text.Json).</summary>
    public string? ParametersJson { get; set; }
    /// <summary>The requested format.</summary>
    public ExportFormat Format { get; set; }
    /// <summary>The lifecycle status.</summary>
    public ExportRunStatus Status { get; set; }
    /// <summary>Whether soft-deleted rows were requested.</summary>
    public bool IncludeSoftDeleted { get; set; }
    /// <summary>The Storage object key once produced.</summary>
    public string? StorageKey { get; set; }
    /// <summary>The suggested download file name.</summary>
    public string? FileName { get; set; }
    /// <summary>The produced byte length.</summary>
    public long? SizeBytes { get; set; }
    /// <summary>When the stored bytes expire (retention).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>The error message on failure.</summary>
    public string? Error { get; set; }
    /// <summary>When the run reached a terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Sets the identity (factory use).</summary>
    public void SetId(Guid id) => Id = id;
}
```

- [ ] **Step 2: Create `Entities/ExportSchedule.cs`:**

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Entities;

/// <summary>A recurring export schedule (cron) that produces runs.</summary>
public sealed class ExportSchedule : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }
    /// <summary>The user notified for each produced run.</summary>
    public string? UserId { get; set; }
    /// <summary>The export definition key.</summary>
    public string DefinitionKey { get; set; } = string.Empty;
    /// <summary>The fixed filter/scope parameters (relative values resolved at fire time).</summary>
    public string? ParametersJson { get; set; }
    /// <summary>The requested format.</summary>
    public ExportFormat Format { get; set; }
    /// <summary>The Quartz cron expression.</summary>
    public string Cron { get; set; } = string.Empty;
    /// <summary>Whether the schedule is active.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Whether produced runs include soft-deleted rows.</summary>
    public bool IncludeSoftDeleted { get; set; }

    /// <summary>Sets the identity (factory use).</summary>
    public void SetId(Guid id) => Id = id;
}
```

- [ ] **Step 3: Create the two EF configurations** (snake_case columns are applied by `UseSnakeCaseNamingConvention()`, but set the table + schema + enum conversions + indexes explicitly).

`EntityConfiguration/ExportRunConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.EntityConfiguration;

internal sealed class ExportRunConfiguration : IEntityTypeConfiguration<ExportRun>
{
    public void Configure(EntityTypeBuilder<ExportRun> b)
    {
        b.ToTable("export_runs", "export");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(100);
        b.Property(x => x.UserId).HasMaxLength(100);
        b.Property(x => x.DefinitionKey).IsRequired().HasMaxLength(200);
        b.Property(x => x.Format).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.StorageKey).HasMaxLength(400);
        b.Property(x => x.FileName).HasMaxLength(260);
        b.HasIndex(x => new { x.TenantId, x.Status }).HasDatabaseName("ix_export_runs_tenant_status");
        b.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_export_runs_expires_at");
    }
}
```

`EntityConfiguration/ExportScheduleConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.EntityConfiguration;

internal sealed class ExportScheduleConfiguration : IEntityTypeConfiguration<ExportSchedule>
{
    public void Configure(EntityTypeBuilder<ExportSchedule> b)
    {
        b.ToTable("export_schedules", "export");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(100);
        b.Property(x => x.UserId).HasMaxLength(100);
        b.Property(x => x.DefinitionKey).IsRequired().HasMaxLength(200);
        b.Property(x => x.Format).HasConversion<int>();
        b.Property(x => x.Cron).IsRequired().HasMaxLength(120);
        b.HasIndex(x => new { x.TenantId, x.Enabled }).HasDatabaseName("ix_export_schedules_tenant_enabled");
    }
}
```

- [ ] **Step 4: Create `ExportDbContext.cs`** (tenant filter reads the ambient accessor; the job sets it — Task 7):

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.EntityConfiguration;

namespace Themia.Modules.Export;

/// <summary>EF context for the export module's own tenant-scoped tables.</summary>
public sealed class ExportDbContext : ThemiaDbContext
{
    /// <summary>Creates the context. Tenant is resolved from the ambient accessor (set per run by the job).</summary>
    public ExportDbContext(DbContextOptions<ExportDbContext> options, ITenantContext? tenantContext = null, IDataFilterScope? filterScope = null)
        : base(options, tenantContext)
    {
        _ = filterScope; // referenced for DI symmetry; query filters read DataFilterScope's ambient flag.
    }

    /// <summary>Export run records.</summary>
    public DbSet<ExportRun> Runs => Set<ExportRun>();

    /// <summary>Export schedules.</summary>
    public DbSet<ExportSchedule> Schedules => Set<ExportSchedule>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ExportRunConfiguration());
        modelBuilder.ApplyConfiguration(new ExportScheduleConfiguration());
        base.OnModelCreating(modelBuilder);
    }
}
```

Confirm `ThemiaDbContext`'s constructor signature accepts `(DbContextOptions, ITenantContext?)`; if the `IDataFilterScope` is not a ctor parameter there, drop the third parameter. The query filter's soft-delete clause already reads `DataFilterScope.SoftDeleteBypassedAmbient` statically (Task 2), so the context needs no filter-scope injection.

- [ ] **Step 5: Create `Migrations/ExportSchemaMigration.cs`** (mirror `SchedulingSchemaMigration`'s `IfDatabase(...).Supported` structure; one migration, three engines):

```csharp
using FluentMigrator;

namespace Themia.Modules.Export.Migrations;

/// <summary>Creates the export schema and its two tables on SQL Server, MySQL, and PostgreSQL.</summary>
[Migration(202606270001)]
public sealed class ExportSchemaMigration : Migration
{
    /// <inheritdoc />
    public override void Up()
    {
        if (IfDatabase("Postgres").Supported && !Schema.Schema("export").Exists())
        {
            Execute.Sql("CREATE SCHEMA IF NOT EXISTS export;");
        }
        if (IfDatabase("SqlServer").Supported)
        {
            Execute.Sql("IF SCHEMA_ID(N'export') IS NULL EXEC(N'CREATE SCHEMA export');");
        }

        CreateRuns();
        CreateSchedules();
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("export_schedules").InSchema("export");
        Delete.Table("export_runs").InSchema("export");
    }

    private void CreateRuns()
    {
        Create.Table("export_runs").InSchema("export")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsString(100).Nullable()
            .WithColumn("definition_key").AsString(200).NotNullable()
            .WithColumn("parameters_json").AsString(int.MaxValue).Nullable()
            .WithColumn("format").AsInt32().NotNullable()
            .WithColumn("status").AsInt32().NotNullable()
            .WithColumn("include_soft_deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("storage_key").AsString(400).Nullable()
            .WithColumn("file_name").AsString(260).Nullable()
            .WithColumn("size_bytes").AsInt64().Nullable()
            .WithColumn("expires_at").AsDateTimeOffset().Nullable()
            .WithColumn("error").AsString(int.MaxValue).Nullable()
            .WithColumn("completed_at").AsDateTimeOffset().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable()
            .WithColumn("is_deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_by").AsString(100).Nullable()
            .WithColumn("restored_at").AsDateTimeOffset().Nullable()
            .WithColumn("restored_by").AsString(100).Nullable();

        Create.Index("ix_export_runs_tenant_status").OnTable("export_runs").InSchema("export")
            .OnColumn("tenant_id").Ascending().OnColumn("status").Ascending();
        Create.Index("ix_export_runs_expires_at").OnTable("export_runs").InSchema("export")
            .OnColumn("expires_at").Ascending();
    }

    private void CreateSchedules()
    {
        Create.Table("export_schedules").InSchema("export")
            .WithColumn("id").AsGuid().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsString(100).Nullable()
            .WithColumn("definition_key").AsString(200).NotNullable()
            .WithColumn("parameters_json").AsString(int.MaxValue).Nullable()
            .WithColumn("format").AsInt32().NotNullable()
            .WithColumn("cron").AsString(120).NotNullable()
            .WithColumn("enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("include_soft_deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable()
            .WithColumn("is_deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_by").AsString(100).Nullable()
            .WithColumn("restored_at").AsDateTimeOffset().Nullable()
            .WithColumn("restored_by").AsString(100).Nullable();

        Create.Index("ix_export_schedules_tenant_enabled").OnTable("export_schedules").InSchema("export")
            .OnColumn("tenant_id").Ascending().OnColumn("enabled").Ascending();
    }
}
```

Cross-check column types/`schema` creation against `SchedulingSchemaMigration` and `NotificationsSchemaMigration` — match their `IfDatabase` idiom exactly (MySQL has no schemas; FluentMigrator maps `InSchema("export")` to a database/prefix per provider — verify the existing migrations' handling for MySQL and copy it; if they omit `InSchema` under MySQL, do the same here).

- [ ] **Step 6: Write the migration test** `ExportSchemaMigrationTests.cs` (PostgreSQL Testcontainer; mirror `MigrationTests`'s fixture):

```csharp
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportSchemaMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder("postgres:16-alpine").WithCleanUp(true).Build();

    public async Task InitializeAsync() => await container.StartAsync();
    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public void Migration_creates_export_tables()
    {
        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb
                .AddPostgres()
                .WithGlobalConnectionString(container.GetConnectionString())
                .ScanIn(typeof(Migrations.ExportSchemaMigration).Assembly).For.Migrations())
            .BuildServiceProvider(false);

        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp(); // throws on failure

        Assert.True(runner.HasMigrationsToApplyDown(202606270001)); // applied and reversible
    }
}
```

Add `Testcontainers.PostgreSql`, `Npgsql`, `FluentMigrator.Runner`, `FluentMigrator.Runner.Postgres` to the test csproj (versions via CPM — confirm pins exist).

- [ ] **Step 7: Build + test.**

```bash
dotnet build src/modules/Themia.Modules.Export --no-incremental
dotnet test tests/Themia.Modules.Export.Tests --filter ExportSchemaMigrationTests
```
Expected: build clean (add new public types to `PublicAPI.Unshipped.txt`); migration test PASS (Docker required).

- [ ] **Step 8: Commit.**

```bash
git add src/modules/Themia.Modules.Export tests/Themia.Modules.Export.Tests
git commit -m "feat: export entities, EF config, DbContext, FluentMigrator schema"
```

---

## Task 6: Run store

**Files:**
- Create: `Store/IExportRunStore.cs`, `Store/ExportRunStore.cs`
- Test: `tests/Themia.Modules.Export.Tests/ExportRunStoreTests.cs` (Testcontainers PostgreSQL + `ExportDbContext`)

**Interfaces:**
- Consumes: `ExportDbContext`, `ExportRun`, `IDataFilterScope`, `TenantContextAccessor`.
- Produces:
  - `Task<ExportRun> CreateAsync(ExportRun run, CancellationToken ct)`
  - `Task<ExportRun?> GetByIdIgnoringTenantAsync(Guid id, CancellationToken ct)` (uses `BypassTenantFilter` — keyed by an unguessable GUID; the job resolves the tenant from it)
  - `Task UpdateAsync(ExportRun run, CancellationToken ct)`
  - `Task<IReadOnlyList<ExportRun>> ListAsync(string? userId, CancellationToken ct)` (tenant-scoped)
  - `Task<IReadOnlyList<ExportRun>> FindExpiredAcrossTenantsAsync(DateTimeOffset now, CancellationToken ct)` (uses `BypassTenantFilter`)

- [ ] **Step 1: Write the failing test** `ExportRunStoreTests.cs` (one representative behavior — cross-tenant GUID lookup for the job):

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportRunStoreTests : IClassFixture<ExportDbFixture>
{
    private readonly ExportDbFixture fixture;
    public ExportRunStoreTests(ExportDbFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task GetByIdIgnoringTenant_finds_run_without_ambient_tenant()
    {
        await fixture.ResetAsync();
        var id = Guid.NewGuid();
        TenantContextAccessor.CurrentTenantId = new TenantId("acme");
        await using (var ctx = fixture.NewContext())
        {
            var store = new ExportRunStore(ctx, new DataFilterScope());
            await store.CreateAsync(new ExportRun { Format = ExportFormat.Csv, Status = ExportRunStatus.Pending, DefinitionKey = "k", TenantId = new TenantId("acme"), CreatedAt = DateTimeOffset.UtcNow }.WithId(id), default);
        }

        TenantContextAccessor.CurrentTenantId = null; // background: no ambient tenant yet
        await using (var ctx = fixture.NewContext())
        {
            var store = new ExportRunStore(ctx, new DataFilterScope());
            var run = await store.GetByIdIgnoringTenantAsync(id, default);
            Assert.NotNull(run);
            Assert.Equal(new TenantId("acme"), run!.TenantId);
        }
    }
}
```

`ExportDbFixture` is a Testcontainers PostgreSQL fixture exposing `NewContext()` (builds `ExportDbContext` with `UseNpgsql(...).UseSnakeCaseNamingConvention()` and runs `ExportSchemaMigration`) and `ResetAsync()`. Add `WithId(Guid)` as a tiny test extension calling `SetId`. Reuse the fixture across Tasks 6–9.

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/Themia.Modules.Export.Tests --filter ExportRunStoreTests`
Expected: FAIL — `IExportRunStore`/`ExportRunStore` do not exist.

- [ ] **Step 3: Create `Store/IExportRunStore.cs`:**

```csharp
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.Store;

/// <summary>Persistence for export runs. Tenant-scoped except the two deliberate cross-tenant reads
/// the background jobs need (keyed by unguessable GUIDs / used only to discover the owning tenant).</summary>
public interface IExportRunStore
{
    /// <summary>Inserts a new run.</summary>
    Task<ExportRun> CreateAsync(ExportRun run, CancellationToken cancellationToken);
    /// <summary>Loads a run by id across tenants (the job then establishes that tenant's scope).</summary>
    Task<ExportRun?> GetByIdIgnoringTenantAsync(Guid id, CancellationToken cancellationToken);
    /// <summary>Persists status/result changes.</summary>
    Task UpdateAsync(ExportRun run, CancellationToken cancellationToken);
    /// <summary>Lists the current tenant's runs (optionally filtered by user), newest first.</summary>
    Task<IReadOnlyList<ExportRun>> ListAsync(string? userId, CancellationToken cancellationToken);
    /// <summary>Finds succeeded runs whose bytes have expired, across all tenants (for cleanup).</summary>
    Task<IReadOnlyList<ExportRun>> FindExpiredAcrossTenantsAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Create `Store/ExportRunStore.cs`:**

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.Store;

internal sealed class ExportRunStore(ExportDbContext db, IDataFilterScope filterScope) : IExportRunStore
{
    public async Task<ExportRun> CreateAsync(ExportRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return run;
    }

    public async Task<ExportRun?> GetByIdIgnoringTenantAsync(Guid id, CancellationToken cancellationToken)
    {
        using (filterScope.BypassTenantFilter())
        {
            return await db.Runs.FirstOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateAsync(ExportRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        db.Runs.Update(run);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExportRun>> ListAsync(string? userId, CancellationToken cancellationToken)
    {
        var q = db.Runs.AsNoTracking();
        if (userId is not null)
        {
            q = q.Where(r => r.UserId == userId);
        }

        return await q.OrderByDescending(r => r.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExportRun>> FindExpiredAcrossTenantsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using (filterScope.BypassTenantFilter())
        {
            return await db.Runs
                .Where(r => r.Status == ExportRunStatus.Succeeded && r.ExpiresAt != null && r.ExpiresAt < now)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 5: Run the test.**

Run: `dotnet test tests/Themia.Modules.Export.Tests --filter ExportRunStoreTests`
Expected: PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/modules/Themia.Modules.Export tests/Themia.Modules.Export.Tests
git commit -m "feat: export run store with tenant-scoped + deliberate cross-tenant reads"
```

---

## Task 7: Background tenant scope + ExportJob

**Files:**
- Create: `Jobs/BackgroundTenantScope.cs`, `Jobs/ExportJob.cs`
- Test: `tests/Themia.Modules.Export.Tests/ExportJobTests.cs` (fakes for `ITenantStorage`, `INotificationDispatcher`, a stub `IExportDefinition`; real `ExportDbContext` via the fixture)

**Interfaces:**
- Consumes: `IExportRunStore`, `IExportDefinitionRegistry` (defined inline here — a keyed lookup), `ITenantStorage`, `INotificationDispatcher`, `IDataFilterScope`, `ExportModuleOptions` (Task 10 defines the options type; for this task define a minimal `ExportModuleOptions` with `Retention`, `LinkTtl` and extend in Task 10), `TenantContextAccessor`.
- Produces: `ExportJob : IJob` keyed by `runId` in the job data map; `BackgroundTenantScope.Begin(TenantId?) -> IDisposable` that sets/restores `TenantContextAccessor.CurrentTenantId`.

- [ ] **Step 1: Create `Jobs/BackgroundTenantScope.cs`:**

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Jobs;

/// <summary>Establishes ambient tenant context for a background (request-less) code path, restoring the
/// previous value on dispose. The EF tenant query filter reads <see cref="TenantContextAccessor"/>.</summary>
internal static class BackgroundTenantScope
{
    public static IDisposable Begin(TenantId? tenantId)
    {
        var previous = TenantContextAccessor.CurrentTenantId;
        TenantContextAccessor.CurrentTenantId = tenantId;
        return new Restore(() => TenantContextAccessor.CurrentTenantId = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
```

- [ ] **Step 2: Define the definition registry** `Definitions/ExportDefinitionRegistry.cs`:

```csharp
namespace Themia.Modules.Export.Definitions;

/// <summary>Resolves a registered <see cref="IExportDefinition"/> by key.</summary>
public interface IExportDefinitionRegistry
{
    /// <summary>Returns the definition for <paramref name="key"/>, or null if none is registered.</summary>
    IExportDefinition? Find(string key);
}

internal sealed class ExportDefinitionRegistry(IEnumerable<IExportDefinition> definitions) : IExportDefinitionRegistry
{
    private readonly Dictionary<string, IExportDefinition> map =
        definitions.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public IExportDefinition? Find(string key) => map.GetValueOrDefault(key);
}
```

- [ ] **Step 3: Write the failing test** `ExportJobTests.cs`:

```csharp
using Quartz;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportJobTests : IClassFixture<ExportDbFixture>
{
    private readonly ExportDbFixture fixture;
    public ExportJobTests(ExportDbFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Succeeds_writes_storage_sets_status_and_notifies()
    {
        await fixture.ResetAsync();
        var id = Guid.NewGuid();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        await using (var ctx = fixture.NewContext())
        {
            await new ExportRunStore(ctx, new DataFilterScope())
                .CreateAsync(new ExportRun { Format = ExportFormat.Csv, Status = ExportRunStatus.Pending, DefinitionKey = "stub", UserId = "u1", TenantId = new TenantId("acme"), CreatedAt = DateTimeOffset.UtcNow }.WithId(id), default);
        }

        var storage = new FakeTenantStorage();
        var notifier = new FakeDispatcher();
        var job = fixture.BuildExportJob(storage, notifier, new StubDefinition());

        await job.Execute(FakeJobContext.WithRunId(id));

        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read, new DataFilterScope()).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Succeeded, run!.Status);
            Assert.NotNull(run.StorageKey);
            Assert.NotNull(run.ExpiresAt);
        }
        Assert.True(storage.PutCalled);
        Assert.True(notifier.Dispatched);
    }
}
```

Provide the small test doubles in the test project: `StubDefinition : IExportDefinition` (returns a fixed `ExportResult`), `FakeTenantStorage : ITenantStorage` (records `PutCalled`, returns a `StoredObject`, `GetDownloadUrlAsync` returns `new Uri("https://x/dl")`), `FakeDispatcher : INotificationDispatcher` (records `Dispatched`), and `FakeJobContext`/`fixture.BuildExportJob(...)` helpers wiring `ExportJob` with the fixture's `ExportDbContext`, an `ExportRunStore`, an `ExportDefinitionRegistry` containing the stub, the fakes, a `DataFilterScope`, and an `ExportModuleOptions { Retention = TimeSpan.FromDays(7), LinkTtl = TimeSpan.FromHours(1) }`.

- [ ] **Step 4: Run to verify it fails.**

Run: `dotnet test tests/Themia.Modules.Export.Tests --filter ExportJobTests`
Expected: FAIL — `ExportJob` does not exist.

- [ ] **Step 5: Create `Jobs/ExportJob.cs`:**

```csharp
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Store;
using Themia.Modules.Notifications.Dispatch;
using Themia.Modules.Storage;
using Themia.Notifications; // NotificationChannel
using Themia.Framework.Data.Abstractions.Filtering;

namespace Themia.Modules.Export.Jobs;

/// <summary>Runs one export: resolve definition, produce bytes, persist to Storage, notify. Quartz job
/// data carries <c>runId</c>. Any exception marks the run Failed and notifies (no retry).</summary>
[DisallowConcurrentExecution]
internal sealed class ExportJob(
    IExportRunStore store,
    IExportDefinitionRegistry registry,
    ITenantStorage storage,
    INotificationDispatcher notifier,
    IDataFilterScope filterScope,
    IOptions<ExportModuleOptions> options,
    ILogger<ExportJob> logger) : IJob
{
    public const string RunIdKey = "runId";

    public async Task Execute(IJobExecutionContext context)
    {
        var runId = Guid.Parse(context.MergedJobDataMap.GetString(RunIdKey)!);
        var run = await store.GetByIdIgnoringTenantAsync(runId, context.CancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            logger.LogWarning("Export run {RunId} not found; skipping.", runId);
            return;
        }

        using var _ = BackgroundTenantScope.Begin(run.TenantId);
        try
        {
            run.Status = ExportRunStatus.Running;
            await store.UpdateAsync(run, context.CancellationToken).ConfigureAwait(false);

            var definition = registry.Find(run.DefinitionKey)
                ?? throw new InvalidOperationException($"No export definition registered for key '{run.DefinitionKey}'.");

            var exportContext = new ExportContext
            {
                TenantId = run.TenantId,
                UserId = run.UserId,
                ParametersJson = run.ParametersJson,
                Format = run.Format,
                FileName = run.FileName,
                IncludeSoftDeleted = run.IncludeSoftDeleted,
            };

            var result = run.IncludeSoftDeleted && definition.AllowsIncludeSoftDeleted
                ? await RunWithSoftDeleteAsync(definition, exportContext, context.CancellationToken).ConfigureAwait(false)
                : await definition.ExportAsync(exportContext, context.CancellationToken).ConfigureAwait(false);

            var key = $"exports/{run.TenantId?.Value ?? "global"}/{run.Id:N}{Extension(run.Format)}";
            using (var ms = new MemoryStream(result.Content))
            {
                await storage.PutAsync(key, ms, new StoragePutOptions(result.ContentType), context.CancellationToken).ConfigureAwait(false);
            }

            run.StorageKey = key;
            run.FileName = result.FileName;
            run.SizeBytes = result.Content.LongLength;
            run.ExpiresAt = DateTimeOffset.UtcNow + options.Value.Retention;
            run.Status = ExportRunStatus.Succeeded;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(run, context.CancellationToken).ConfigureAwait(false);

            await NotifyAsync(run, key, succeeded: true, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Export run {RunId} failed.", run.Id);
            run.Status = ExportRunStatus.Failed;
            run.Error = ex.Message;
            run.CompletedAt = DateTimeOffset.UtcNow;
            await store.UpdateAsync(run, context.CancellationToken).ConfigureAwait(false);
            await NotifyAsync(run, storageKey: null, succeeded: false, context.CancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Themia.Export.ExportResult> RunWithSoftDeleteAsync(
        IExportDefinition definition, ExportContext ctx, CancellationToken ct)
    {
        using (filterScope.BypassSoftDeleteFilter())
        {
            return await definition.ExportAsync(ctx, ct).ConfigureAwait(false);
        }
    }

    private async Task NotifyAsync(ExportRun run, string? storageKey, bool succeeded, CancellationToken ct)
    {
        if (run.UserId is null)
        {
            return;
        }

        string body;
        if (succeeded && storageKey is not null)
        {
            var url = await storage.GetDownloadUrlAsync(storageKey, options.Value.LinkTtl, ct).ConfigureAwait(false);
            body = $"Your export '{run.DefinitionKey}' is ready: {url}";
        }
        else
        {
            body = $"Your export '{run.DefinitionKey}' failed: {run.Error}";
        }

        await notifier.DispatchAsync(
            new NotificationRequest
            {
                UserId = run.UserId,
                Channels = [NotificationChannel.Email, NotificationChannel.InApp],
                Subject = succeeded ? "Export ready" : "Export failed",
                Body = body,
            },
            ct).ConfigureAwait(false);
    }

    private static string Extension(ExportFormat format) =>
        format == ExportFormat.Xlsx ? ".xlsx" : ".csv";
}
```

Verify member names against the verified signatures: `ITenantStorage.PutAsync(string,Stream,StoragePutOptions,CT)`, `GetDownloadUrlAsync(string,TimeSpan,CT)->Task<Uri>`, `StoragePutOptions(string ContentType,...)`, `INotificationDispatcher.DispatchAsync(NotificationRequest,CT)->ValueTask`, `NotificationRequest { required UserId, required Channels, Subject, Body }`, `NotificationChannel.Email/InApp`. Adjust namespaces to the actual ones (`Themia.Modules.Storage` for `ITenantStorage`/`StoragePutOptions`; `Themia.Notifications` for `NotificationChannel`).

- [ ] **Step 6: Run the test.**

Run: `dotnet test tests/Themia.Modules.Export.Tests --filter ExportJobTests`
Expected: PASS — status Succeeded, storage written, notification dispatched.

- [ ] **Step 7: Add a failure-path fact** (a `ThrowingDefinition` whose `ExportAsync` throws → assert `Failed` + `notifier.Dispatched` with the failure subject), run it, confirm PASS.

- [ ] **Step 8: Commit.**

```bash
git add src/modules/Themia.Modules.Export tests/Themia.Modules.Export.Tests
git commit -m "feat: ExportJob + background tenant scope + definition registry"
```

---

## Task 8: Request service + Quartz scheduling

**Files:**
- Create: `Requests/ExportSubmission.cs`, `Requests/ExportScheduleRequest.cs`, `Requests/ExportRunView.cs`, `Requests/IExportRequestService.cs`, `Requests/ExportRequestService.cs`
- Test: `tests/Themia.Modules.Export.Tests/ExportRequestServiceTests.cs`

**Interfaces:**
- Consumes: `IExportRunStore`, `IExportDefinitionRegistry`, `ISchedulerFactory` (Quartz), `ITenantContext`.
- Produces:
  - `IExportRequestService.SubmitAsync(ExportSubmission, CancellationToken) -> Task<ExportRunView>` (creates a `Pending` run, schedules a one-shot `ExportJob` firing now)
  - `IExportRequestService.ScheduleAsync(ExportScheduleRequest, CancellationToken) -> Task<Guid>` (persists schedule + registers a cron trigger keyed by scheduleId)
  - `IExportRequestService.ListRunsAsync(string? userId, CancellationToken) -> Task<IReadOnlyList<ExportRunView>>`
  - `IExportRequestService.GetRunAsync(Guid, CancellationToken) -> Task<ExportRunView?>`
  - `sealed record ExportSubmission(string DefinitionKey, string? ParametersJson, ExportFormat Format, string? FileName = null, bool IncludeSoftDeleted = false, string? UserId = null)`
  - `sealed record ExportScheduleRequest(string DefinitionKey, string Cron, ExportFormat Format, string? ParametersJson = null, bool IncludeSoftDeleted = false, string? UserId = null)`
  - `sealed record ExportRunView(Guid Id, string DefinitionKey, ExportFormat Format, ExportRunStatus Status, string? StorageKey, long? SizeBytes, DateTimeOffset? ExpiresAt, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)`

- [ ] **Step 1: Write the failing test** `ExportRequestServiceTests.cs`:

```csharp
using Quartz;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Export;
using Themia.Modules.Export.Requests;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportRequestServiceTests : IClassFixture<ExportDbFixture>
{
    private readonly ExportDbFixture fixture;
    public ExportRequestServiceTests(ExportDbFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Submit_creates_pending_run_and_schedules_one_shot_job()
    {
        await fixture.ResetAsync();
        var scheduler = await fixture.NewMemoryScheduler();
        var service = fixture.BuildRequestService(scheduler, tenant: new TenantId("acme"), definitions: ["sales"]);

        var view = await service.SubmitAsync(new ExportSubmission("sales", null, ExportFormat.Csv, UserId: "u1"), default);

        Assert.Equal(ExportRunStatus.Pending, view.Status);
        var keys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.AnyGroup());
        Assert.NotEmpty(keys); // a one-shot job was scheduled
    }

    [Fact]
    public async Task Submit_rejects_soft_delete_when_definition_disallows()
    {
        await fixture.ResetAsync();
        var scheduler = await fixture.NewMemoryScheduler();
        var service = fixture.BuildRequestService(scheduler, tenant: new TenantId("acme"), definitions: ["sales"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(new ExportSubmission("sales", null, ExportFormat.Csv, IncludeSoftDeleted: true), default));
    }

    [Fact]
    public async Task Submit_rejects_unknown_definition_key()
    {
        await fixture.ResetAsync();
        var scheduler = await fixture.NewMemoryScheduler();
        var service = fixture.BuildRequestService(scheduler, tenant: new TenantId("acme"), definitions: ["sales"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(new ExportSubmission("nope", null, ExportFormat.Csv), default));
    }
}
```

`fixture.NewMemoryScheduler()` returns a Quartz in-memory `IScheduler` (via `StdSchedulerFactory`); `fixture.BuildRequestService(...)` wires `ExportRequestService` with the store, a registry whose `sales` definition has `AllowsIncludeSoftDeleted == false`, an `ISchedulerFactory` returning that scheduler, and a `TenantContext(tenant)`.

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/Themia.Modules.Export.Tests --filter ExportRequestServiceTests`
Expected: FAIL — service types do not exist.

- [ ] **Step 3: Create the record types** (`ExportSubmission`, `ExportScheduleRequest`, `ExportRunView`) exactly as in the Interfaces block above, each in its own file with XML docs.

- [ ] **Step 4: Create `Requests/IExportRequestService.cs`** with the four members + XML docs (signatures as above).

- [ ] **Step 5: Create `Requests/ExportRequestService.cs`:**

```csharp
using Quartz;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Store;

namespace Themia.Modules.Export.Requests;

internal sealed class ExportRequestService(
    IExportRunStore store,
    IExportDefinitionRegistry registry,
    ISchedulerFactory schedulerFactory,
    ITenantContext tenantContext) : IExportRequestService
{
    public async Task<ExportRunView> SubmitAsync(ExportSubmission submission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var definition = registry.Find(submission.DefinitionKey)
            ?? throw new InvalidOperationException($"No export definition registered for key '{submission.DefinitionKey}'.");
        if (submission.IncludeSoftDeleted && !definition.AllowsIncludeSoftDeleted)
        {
            throw new InvalidOperationException($"Definition '{submission.DefinitionKey}' does not allow including soft-deleted rows.");
        }

        var run = new ExportRun
        {
            TenantId = tenantContext.CurrentTenantId,
            UserId = submission.UserId,
            DefinitionKey = submission.DefinitionKey,
            ParametersJson = submission.ParametersJson,
            Format = submission.Format,
            FileName = submission.FileName,
            IncludeSoftDeleted = submission.IncludeSoftDeleted,
            Status = ExportRunStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        run.SetId(Guid.NewGuid());
        await store.CreateAsync(run, cancellationToken).ConfigureAwait(false);

        var scheduler = await schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var job = JobBuilder.Create<ExportJob>()
            .WithIdentity($"export-{run.Id:N}", "export")
            .UsingJobData(ExportJob.RunIdKey, run.Id.ToString())
            .Build();
        var trigger = TriggerBuilder.Create().StartNow().Build();
        await scheduler.ScheduleJob(job, trigger, cancellationToken).ConfigureAwait(false);

        return ToView(run);
    }

    public async Task<Guid> ScheduleAsync(ExportScheduleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var definition = registry.Find(request.DefinitionKey)
            ?? throw new InvalidOperationException($"No export definition registered for key '{request.DefinitionKey}'.");
        if (request.IncludeSoftDeleted && !definition.AllowsIncludeSoftDeleted)
        {
            throw new InvalidOperationException($"Definition '{request.DefinitionKey}' does not allow including soft-deleted rows.");
        }

        var schedule = new ExportSchedule
        {
            TenantId = tenantContext.CurrentTenantId,
            UserId = request.UserId,
            DefinitionKey = request.DefinitionKey,
            ParametersJson = request.ParametersJson,
            Format = request.Format,
            Cron = request.Cron,
            Enabled = true,
            IncludeSoftDeleted = request.IncludeSoftDeleted,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        schedule.SetId(Guid.NewGuid());

        // Persist via the same DbContext the store uses (add an ICreate for schedules to the store, or a
        // dedicated IExportScheduleStore mirroring ExportRunStore.CreateAsync). Then register the cron trigger:
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var job = JobBuilder.Create<ExportScheduleJob>()
            .WithIdentity($"export-schedule-{schedule.Id:N}", "export")
            .UsingJobData(ExportScheduleJob.ScheduleIdKey, schedule.Id.ToString())
            .Build();
        var trigger = TriggerBuilder.Create().WithCronSchedule(schedule.Cron).Build();
        await scheduler.ScheduleJob(job, trigger, cancellationToken).ConfigureAwait(false);

        return schedule.Id;
    }

    public async Task<IReadOnlyList<ExportRunView>> ListRunsAsync(string? userId, CancellationToken cancellationToken)
    {
        var runs = await store.ListAsync(userId, cancellationToken).ConfigureAwait(false);
        return runs.Select(ToView).ToList();
    }

    public async Task<ExportRunView?> GetRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var runs = await store.ListAsync(userId: null, cancellationToken).ConfigureAwait(false);
        var run = runs.FirstOrDefault(r => r.Id == id);
        return run is null ? null : ToView(run);
    }

    private static ExportRunView ToView(ExportRun r) =>
        new(r.Id, r.DefinitionKey, r.Format, r.Status, r.StorageKey, r.SizeBytes, r.ExpiresAt, r.Error, r.CreatedAt, r.CompletedAt);
}
```

**Note for the implementer:** this introduces two follow-on needs handled in this task:
1. Add an `IExportScheduleStore` (or extend `IExportRunStore`) with `CreateScheduleAsync` + `GetScheduleByIdIgnoringTenantAsync`, mirroring `ExportRunStore` (same `BypassTenantFilter` pattern for the by-id lookup). Persist the schedule before registering the trigger.
2. Add `Jobs/ExportScheduleJob.cs : IJob` with `const string ScheduleIdKey = "scheduleId"`: load the schedule cross-tenant, `BackgroundTenantScope.Begin(schedule.TenantId)`, resolve any relative params against `DateTimeOffset.UtcNow`, then create a run + invoke the same Submit→ExportJob path (simplest: call `IExportRequestService.SubmitAsync` with the schedule's fields, or factor the run-creation+enqueue into a shared internal method both `SubmitAsync` and `ExportScheduleJob` call). Keep relative-param resolution as a documented extension point: if `TParams` carries relative markers, the definition resolves them in `RowsAsync` using `DateTimeOffset.UtcNow` — no schedule-side date math needed for v1.

- [ ] **Step 6: Run the tests.**

Run: `dotnet test tests/Themia.Modules.Export.Tests --filter ExportRequestServiceTests`
Expected: PASS (3 facts).

- [ ] **Step 7: Commit.**

```bash
git add src/modules/Themia.Modules.Export tests/Themia.Modules.Export.Tests
git commit -m "feat: export request service + Quartz one-shot and cron scheduling"
```

---

## Task 9: Cleanup job

**Files:**
- Create: `Jobs/CleanupJob.cs`
- Test: `tests/Themia.Modules.Export.Tests/CleanupJobTests.cs`

**Interfaces:**
- Consumes: `IExportRunStore.FindExpiredAcrossTenantsAsync`, `ITenantStorage`, `BackgroundTenantScope`.
- Produces: `CleanupJob : IJob` that deletes expired blobs per tenant and marks runs `Expired`.

- [ ] **Step 1: Write the failing test** `CleanupJobTests.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class CleanupJobTests : IClassFixture<ExportDbFixture>
{
    private readonly ExportDbFixture fixture;
    public CleanupJobTests(ExportDbFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Deletes_expired_blobs_and_marks_runs_expired()
    {
        await fixture.ResetAsync();
        var id = Guid.NewGuid();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        await using (var ctx = fixture.NewContext())
        {
            await new ExportRunStore(ctx, new DataFilterScope()).CreateAsync(new ExportRun
            {
                TenantId = new TenantId("acme"), DefinitionKey = "k", Format = ExportFormat.Csv,
                Status = ExportRunStatus.Succeeded, StorageKey = "exports/acme/x.csv",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1), CreatedAt = DateTimeOffset.UtcNow.AddDays(-8),
            }.WithId(id), default);
        }

        var storage = new FakeTenantStorage();
        var job = fixture.BuildCleanupJob(storage);
        await job.Execute(FakeJobContext.Empty());

        Assert.Contains("exports/acme/x.csv", storage.Deleted);
        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read, new DataFilterScope()).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Expired, run!.Status);
        }
    }
}
```

Extend `FakeTenantStorage` to record `Deleted` keys.

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/Themia.Modules.Export.Tests --filter CleanupJobTests`
Expected: FAIL — `CleanupJob` does not exist.

- [ ] **Step 3: Create `Jobs/CleanupJob.cs`:**

```csharp
using Microsoft.Extensions.Logging;
using Quartz;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Store;
using Themia.Modules.Storage;

namespace Themia.Modules.Export.Jobs;

/// <summary>Recurring retention sweep: deletes expired export blobs (per tenant) and marks runs Expired.</summary>
[DisallowConcurrentExecution]
internal sealed class CleanupJob(
    IExportRunStore store,
    ITenantStorage storage,
    ILogger<CleanupJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await store.FindExpiredAcrossTenantsAsync(now, context.CancellationToken).ConfigureAwait(false);

        foreach (var group in expired.GroupBy(r => r.TenantId))
        {
            using var _ = BackgroundTenantScope.Begin(group.Key);
            foreach (var run in group)
            {
                try
                {
                    if (run.StorageKey is not null)
                    {
                        await storage.DeleteAsync(run.StorageKey, context.CancellationToken).ConfigureAwait(false);
                    }

                    run.Status = ExportRunStatus.Expired;
                    await store.UpdateAsync(run, context.CancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to clean up expired export run {RunId}.", run.Id);
                }
            }
        }
    }
}
```

The `ITenantStorage` resolved here must observe the per-group tenant scope — register it scoped and resolve a fresh instance inside the loop if the storage implementation captures tenant at construction; otherwise the ambient-tenant set by `BackgroundTenantScope` covers it. Verify against `Themia.Modules.Storage`'s tenant-resolution and adjust resolution accordingly (resolve `ITenantStorage` from a scoped provider inside each group if needed).

- [ ] **Step 4: Run the test.**

Run: `dotnet test tests/Themia.Modules.Export.Tests --filter CleanupJobTests`
Expected: PASS.

- [ ] **Step 5: Commit.**

```bash
git add src/modules/Themia.Modules.Export tests/Themia.Modules.Export.Tests
git commit -m "feat: retention cleanup job (per-tenant blob delete + mark expired)"
```

---

## Task 10: Module class, options, DI, scheduler precondition

**Files:**
- Create: `ExportModuleOptions.cs` (finalize), `ExportModule.cs`, `DependencyInjection/ExportModuleServiceCollectionExtensions.cs`
- Test: `tests/Themia.Modules.Export.Tests/ExportModuleTests.cs`

**Interfaces:**
- Produces:
  - `sealed class ExportModuleOptions { TimeSpan Retention = 7d; TimeSpan LinkTtl = 1h; string CleanupCron = "0 0 3 * * ?"; string ConnectionStringName = "Default" }`
  - `sealed class ExportModule : ThemiaModuleBase` (ctor takes `MigrationEngine engine`, optional options)
  - `AddThemiaExportModule(this IServiceCollection, Action<ExportModuleOptions>? configure = null) -> IServiceCollection`

- [ ] **Step 1: Create `ExportModuleOptions.cs`:**

```csharp
namespace Themia.Modules.Export;

/// <summary>Configuration for the export module.</summary>
public sealed class ExportModuleOptions
{
    /// <summary>How long produced files live in Storage before cleanup. Default 7 days.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);
    /// <summary>Signed download-URL lifetime. Default 1 hour.</summary>
    public TimeSpan LinkTtl { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Quartz cron for the cleanup sweep. Default daily at 03:00.</summary>
    public string CleanupCron { get; set; } = "0 0 3 * * ?";
    /// <summary>The configuration connection-string name for the export schema. Default "Default".</summary>
    public string ConnectionStringName { get; set; } = "Default";
}
```

- [ ] **Step 2: Create `DependencyInjection/ExportModuleServiceCollectionExtensions.cs`:**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Export.DependencyInjection;
using Themia.Export.Excel.DependencyInjection;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Requests;
using Themia.Modules.Export.Store;

namespace Themia.Modules.Export.DependencyInjection;

/// <summary>DI entry point for the export module.</summary>
public static class ExportModuleServiceCollectionExtensions
{
    /// <summary>Registers the export module services (definitions registry, store, request service, jobs).</summary>
    public static IServiceCollection AddThemiaExportModule(
        this IServiceCollection services, Action<ExportModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddThemiaExport();
        services.AddThemiaExcelExport();
        services.TryAddSingleton<IDataFilterScope, DataFilterScope>();

        services.AddDbContextFactory<ExportDbContext>((sp, db) =>
        {
            var provider = sp.GetRequiredService<IDatabaseProvider>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ExportModuleOptions>>().Value;
            var cs = configuration.GetConnectionString(options.ConnectionStringName)
                ?? throw new InvalidOperationException($"Connection string '{options.ConnectionStringName}' not found.");
            switch (provider.ProviderName)
            {
                case DatabaseProviderNames.Postgres: db.UseNpgsql(cs); break;
                case DatabaseProviderNames.SqlServer: db.UseSqlServer(cs); break;
                case DatabaseProviderNames.MySql: db.UseMySql(cs, Microsoft.EntityFrameworkCore.ServerVersion.AutoDetect(cs)); break;
            }
            db.UseSnakeCaseNamingConvention();
        });

        services.TryAddScoped<ExportDbContext>(sp => sp.GetRequiredService<IDbContextFactory<ExportDbContext>>().CreateDbContext());
        services.TryAddSingleton<IExportDefinitionRegistry, ExportDefinitionRegistry>();
        services.TryAddScoped<IExportRunStore, ExportRunStore>();
        services.TryAddScoped<IExportRequestService, ExportRequestService>();
        services.AddTransient<ExportJob>();
        services.AddTransient<CleanupJob>();
        return services;
    }
}
```

Reconcile the DbContext provider-switch with `SchedulingModule`'s exact form (provider names + `UseMySql` overload). If the repo standardizes only Postgres/SqlServer in module DbContext registration, match that and leave MySQL DDL to the migration (queries still run; the EF provider for MySQL must be referenced — copy whatever Scheduling does).

- [ ] **Step 3: Create `ExportModule.cs`:**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Migrations;

namespace Themia.Modules.Export;

/// <summary>The export module: runs schema migrations, asserts a Quartz scheduler is present, and
/// registers the recurring cleanup job.</summary>
public sealed class ExportModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly ExportModuleOptions options;

    /// <summary>Creates the module for the given migration engine with default options.</summary>
    public ExportModule(MigrationEngine engine) : this(engine, new ExportModuleOptions()) { }

    /// <summary>Creates the module for the given migration engine and options.</summary>
    public ExportModule(MigrationEngine engine, ExportModuleOptions options)
    {
        this.engine = engine;
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Export",
        displayName: "Export",
        description: "Asynchronous and scheduled tabular export with Storage delivery and completion notifications.",
        version: new Version(0, 7, 0, 0));

    /// <inheritdoc />
    public override async ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        // Precondition: a Quartz scheduler must be available (Scheduling can be configured to register none).
        _ = sp.GetService<ISchedulerFactory>()
            ?? throw new InvalidOperationException("Themia.Modules.Export requires a Quartz ISchedulerFactory; ensure SchedulingModule registers a scheduler.");

        var configuration = sp.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{options.ConnectionStringName}' not found.");
        ThemiaMigrations.Run(engine, connectionString, typeof(ExportSchemaMigration).Assembly);

        // Register the recurring cleanup job.
        var scheduler = await sp.GetRequiredService<ISchedulerFactory>().GetScheduler(cancellationToken).ConfigureAwait(false);
        var job = JobBuilder.Create<CleanupJob>().WithIdentity("export-cleanup", "export").Build();
        var trigger = TriggerBuilder.Create().WithIdentity("export-cleanup-trigger", "export")
            .WithCronSchedule(options.CleanupCron).Build();
        if (!await scheduler.CheckExists(job.Key, cancellationToken).ConfigureAwait(false))
        {
            await scheduler.ScheduleJob(job, trigger, cancellationToken).ConfigureAwait(false);
        }
    }
}
```

Confirm `ThemiaModuleBase`, `ModuleDescriptor`, and `MigrationEngine`/`ThemiaMigrations.Run` namespaces against the extraction (e.g. `Themia.Framework.Core.Modules`, `Themia.Data.Migrations`). Match `NotificationsModule`'s `InitializeAsync` migration idiom exactly.

- [ ] **Step 4: Write the test** `ExportModuleTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportModuleTests
{
    [Fact]
    public async Task InitializeAsync_throws_when_no_scheduler_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var provider = services.BuildServiceProvider();

        var module = new ExportModule(MigrationEngine.Postgres);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await module.InitializeAsync(provider));
    }
}
```

- [ ] **Step 5: Run the test + build clean.**

```bash
dotnet build src/modules/Themia.Modules.Export --no-incremental
dotnet test tests/Themia.Modules.Export.Tests --filter ExportModuleTests
```
Expected: build `0 Warning(s)` (add every new public symbol to `PublicAPI.Unshipped.txt`); test PASS.

- [ ] **Step 6: Commit.**

```bash
git add src/modules/Themia.Modules.Export tests/Themia.Modules.Export.Tests
git commit -m "feat: ExportModule, options, DI, scheduler precondition + cleanup scheduling"
```

---

## Task 11: Docs/catalog + full solution verification

**Files:**
- Modify: `docs/themia-architecture-overview.md` (`Themia.Modules.Export` catalog row → realized async-module shape)
- Modify: `CHANGELOG.md` (if present — add an entry under a new version)

- [ ] **Step 1: Update the catalog row** in `docs/themia-architecture-overview.md` to record the realized two-tier shape (neutral cores + `Themia.Modules.Export` async module + the `BypassSoftDeleteFilter` data-layer addition). Match the existing row format.

- [ ] **Step 2: Add a CHANGELOG entry** (Added: async/scheduled export module; data-layer `BypassSoftDeleteFilter`) if `CHANGELOG.md` exists.

- [ ] **Step 3: Full clean build + full test run.**

```bash
dotnet build Themia.sln --no-incremental
dotnet test Themia.sln
```
Expected: build `0 Warning(s) 0 Error(s)`; all tests PASS (the new module's Testcontainers tests require Docker).

- [ ] **Step 4: Commit.**

```bash
git add docs/themia-architecture-overview.md CHANGELOG.md
git commit -m "docs: record Themia.Modules.Export in the architecture catalog"
```

---

## Self-Review Notes (addressed)

- **Spec coverage:** triggers (Tasks 8 on-demand + recurring), keyed definitions (Task 4), scope/filter via `TParams` (Task 4) + soft-delete opt-in (Tasks 1–3 + 7), link-only delivery (Task 7), retention + cleanup (Tasks 5 schema, 9 job, 10 schedule), notify-on-every-failure no-retry (Task 7), background tenant scope (Task 7), `BypassSoftDeleteFilter` framework addition (Tasks 1–3), scheduler precondition (Task 10), Testcontainers DB tests (Tasks 5,6,7,9). All spec sections map to a task.
- **Type consistency:** `ExportRunStatus`, `ExportFormat`, `ExportContext`, `IExportDefinition.AllowsIncludeSoftDeleted`, `ExportJob.RunIdKey`, `IExportRunStore` member names, and `ExportRunView` fields are used identically across tasks 4–10.
- **Known reconciliations the implementer must verify against the live tree (called out inline):** `ThemiaDbContext` ctor parameters; MySQL `InSchema` handling in FluentMigrator; module DbContext provider-switch (Postgres/SqlServer/MySQL) matching `SchedulingModule`; `ITenantStorage` tenant resolution under per-group cleanup scope; exact namespaces for `ThemiaModuleBase`/`ModuleDescriptor`/`MigrationEngine`. These are integration seams, not placeholders — each has a concrete default plus a one-line verification.
