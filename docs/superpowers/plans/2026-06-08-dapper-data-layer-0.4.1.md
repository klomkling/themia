# Themia Dapper Data Layer 0.4.1 (PostgreSQL) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Ship a Dapper (+ SqlKata) data layer as a net10 sibling of `Themia.Framework.Data.EFCore`, behind
a new shared `Themia.Framework.Data.Abstractions`, so a Dapper-first app gets tenant isolation, audit,
soft-delete, and unit-of-work/transactions on PostgreSQL — with the EF layer retrofitted to the same contracts.

**Architecture:** A provider-agnostic abstractions package (`ISpecification<T>`, repository/UoW, tenant
filter scope, current-user accessor) is implemented twice: a new Dapper core (`ISqlCompiler` seam +
expression→SqlKata translator + tenant-seeded query factory + deferred-write UoW) wired for Postgres in a
`*.PostgreSql` package, and additive adapters over the existing `ThemiaDbContext`. A Testcontainers
conformance suite runs the same behavioural tests against both implementations.

**Tech Stack:** .NET 10, Dapper 2.1.66, SqlKata 2.4.0 (compile-only, behind an internal seam), Npgsql 10.0.2,
xUnit 2.9.3, Testcontainers.PostgreSql 4.12.0. Repo rules: `Nullable=enable`, `TreatWarningsAsErrors=true`,
`GenerateDocumentationFile=true`, central package management, PublicAPI tracking per package.

**Spec:** `docs/superpowers/specs/2026-06-07-themia-dapper-data-layer-design.md` (read it first).

---

## Conventions used throughout this plan

- Run all `dotnet` commands from `Packages/themia/`.
- After each task: build the touched project(s) with `-warnaserror` implied (repo sets it), then run the
  task's tests. New **public** members must be added to that package's `PublicAPI.Unshipped.txt` or the build
  fails `RS0016`; each implementation task that adds public surface includes the exact lines to append.
- snake_case is the column/table convention (matches EF's `UseSnakeCaseNamingConvention`). Dapper read
  mapping relies on `Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true` (set once, Task 2.1).
- Commit after every task with the message shown. Branch is `feat/dapper-data-layer` (already created).

---

## File Structure

### `src/framework/Themia.Framework.Data.Abstractions/` (new, net10)
- `Themia.Framework.Data.Abstractions.csproj` — refs Core; PublicAPI + analyzer.
- `Paging/PagedResult.cs` — `PagedResult<T>(IReadOnlyList<T> Items, long Total, int? Skip, int? Take)`.
- `Specifications/ISpecification.cs` — `ISpecification<T>`, `OrderExpression<T>`.
- `Specifications/Specification.cs` — `Specification<T>` base (fluent Where/OrderBy/Page/IgnoreTenant).
- `Specifications/SpecificationExtensions.cs` — `And`/`Or`/`Not` combinators (expression rebind visitor).
- `Repositories/IReadRepository.cs`, `Repositories/IRepository.cs`.
- `UnitOfWork/IUnitOfWork.cs`, `UnitOfWork/ITransactionScope.cs`.
- `Filtering/IDataFilterScope.cs`.
- `Auditing/ICurrentUserAccessor.cs` — shared audit "who" source (default null impl lives per layer).
- `Exceptions/UnsupportedSpecificationException.cs`.
- `PublicAPI.Shipped.txt` (blank), `PublicAPI.Unshipped.txt`.

### `src/framework/Themia.Framework.Data.Dapper/` (new, net10)
- `Themia.Framework.Data.Dapper.csproj` — refs Abstractions, Core, MultiTenancy; pkgs Dapper, SqlKata.
- `Mapping/EntityMapping.cs` — table/column name conventions, key column, cached `Id` setter.
- `Mapping/EntityMappingRegistry.cs` — per-`T` cache + optional overrides.
- `Sql/ISqlCompiler.cs` — seam over SqlKata (`CompiledSql Compile(Query)`).
- `Sql/CompiledSql.cs` — `(string Sql, IReadOnlyDictionary<string, object?> Parameters)`.
- `Translation/SpecificationTranslator.cs` — expression-tree → SqlKata `Query` predicates/order/paging.
- `Tenancy/TenantPredicate.cs` — applies tenant + soft-delete + bypass to a SqlKata `Query`.
- `Tenancy/TenantQueryFactory.cs` — `ITenantQueryFactory.For<T>()` (tier-2).
- `Connection/IDapperConnectionContext.cs`, `Connection/DapperConnectionContext.cs` — scoped conn + tx.
- `Connection/IDapperConnectionFactory.cs` — engine seam returning a `DbConnection` (impl in PostgreSql pkg).
- `Repositories/DapperReadRepository.cs`, `Repositories/DapperRepository.cs`.
- `UnitOfWork/DapperUnitOfWork.cs` — pending-ops queue + flush + stamping + key population.
- `UnitOfWork/PendingOperation.cs` — Added/Modified/Removed record.
- `Filtering/DataFilterScope.cs` — `AsyncLocal<bool>` tenant-filter toggle (also read by EF adapter).
- `Auditing/NullCurrentUserAccessor.cs` — default `ICurrentUserAccessor` (returns null).
- `DependencyInjection/DapperDataServiceCollectionExtensions.cs` — `AddThemiaDapperCore`.
- PublicAPI files.

### `src/framework/Themia.Framework.Data.Dapper.PostgreSql/` (new, net10)
- `Themia.Framework.Data.Dapper.PostgreSql.csproj` — refs Dapper core; pkgs Npgsql, SqlKata.
- `PostgresSqlCompiler.cs` — wraps SqlKata `PostgresCompiler`.
- `NpgsqlConnectionFactory.cs` — `IDapperConnectionFactory` resolving `ITenantAccessor.Current?.ConnectionString`.
- `DependencyInjection/PostgresDapperServiceCollectionExtensions.cs` — `AddThemiaDapperPostgres`.
- PublicAPI files.

### `src/framework/Themia.Framework.Data.EFCore/` (retrofit, additive)
- `Repositories/EfReadRepository.cs`, `Repositories/EfRepository.cs`.
- `UnitOfWork/EfUnitOfWork.cs`.
- `Extensions/RepositoryServiceCollectionExtensions.cs` — `AddThemiaDataRepositories<TContext>`.
- (no change to `ThemiaDbContext`; the `DataFilterScope` AsyncLocal lives in the Dapper package and EF
  references the Dapper package? — NO. To avoid EF→Dapper dependency, `IDataFilterScope` + the AsyncLocal
  carrier both live in **Abstractions**; see Task 1.7.)

### `tests/`
- `tests/Themia.Framework.Data.Abstractions.Tests/` — Specification combinator unit tests.
- `tests/Themia.Framework.Data.Dapper.Tests/` — translator + mapping + tenant-predicate unit tests.
- `tests/Themia.Framework.Data.Dapper.Conformance/` — abstract conformance base (no fixture).
- `tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/` — runs the conformance base against the
  Dapper-PG impl **and** the EF-PG impl, via Testcontainers.

---

## PHASE 0 — Scaffolding

### Task 0.1: Pin SqlKata + create the Abstractions project

**Files:**
- Modify: `Directory.Packages.props`
- Create: `src/framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj`
- Create: `src/framework/Themia.Framework.Data.Abstractions/PublicAPI.Shipped.txt` (empty)
- Create: `src/framework/Themia.Framework.Data.Abstractions/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Add the SqlKata pin.** In `Directory.Packages.props`, inside the existing `<ItemGroup>` of
  `<PackageVersion>` entries (next to the `Dapper` line), add:

```xml
<PackageVersion Include="SqlKata" Version="2.4.0" />
```

- [ ] **Step 2: Create the csproj.**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Framework.Data.Abstractions</PackageId>
    <Description>Provider-agnostic data-access contracts (specifications, repository, unit of work, tenant filtering) shared by the Themia EF Core and Dapper data layers.</Description>
    <PackageTags>themia;data;abstractions;repository;specification;multi-tenancy</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Core/Themia.Framework.Core.csproj" />
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
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Framework.Data.Abstractions.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `PublicAPI.Shipped.txt`** as an empty file (zero bytes acceptable).
- [ ] **Step 4: Create `PublicAPI.Unshipped.txt`** with a single line:

```
#nullable enable
```

- [ ] **Step 5: Add the project to the solution.**

Run: `dotnet sln Themia.sln add src/framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj`
Expected: `Project ... added to the solution.`

- [ ] **Step 6: Build the empty project.**

Run: `dotnet build src/framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj -c Release`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 7: Commit.**

```bash
git add Directory.Packages.props src/framework/Themia.Framework.Data.Abstractions Themia.sln
git commit -m "chore(data): scaffold Themia.Framework.Data.Abstractions + pin SqlKata"
```

### Task 0.2: Create the Dapper core + PostgreSql projects

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj`
- Create: `src/framework/Themia.Framework.Data.Dapper/PublicAPI.Shipped.txt` (empty) + `PublicAPI.Unshipped.txt` (`#nullable enable`)
- Create: `src/framework/Themia.Framework.Data.Dapper.PostgreSql/Themia.Framework.Data.Dapper.PostgreSql.csproj`
- Create: its `PublicAPI.Shipped.txt` (empty) + `PublicAPI.Unshipped.txt` (`#nullable enable`)

- [ ] **Step 1: Dapper core csproj.**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Framework.Data.Dapper</PackageId>
    <Description>Dapper + SqlKata implementation of the Themia data-access contracts: tenant isolation, audit, soft-delete, unit of work. Engine-agnostic core; add a Themia.Framework.Data.Dapper.&lt;Engine&gt; package for a database.</Description>
    <PackageTags>themia;dapper;sqlkata;data;multi-tenancy;audit;soft-delete</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
    <ProjectReference Include="../Themia.Framework.Core/Themia.Framework.Core.csproj" />
    <ProjectReference Include="../Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Dapper" />
    <PackageReference Include="SqlKata" />
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
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Framework.Data.Dapper.Tests" />
    <InternalsVisibleTo Include="Themia.Framework.Data.Dapper.Conformance" />
    <InternalsVisibleTo Include="Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: PostgreSql csproj.**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Framework.Data.Dapper.PostgreSql</PackageId>
    <Description>PostgreSQL engine for the Themia Dapper data layer (Npgsql + SqlKata PostgresCompiler).</Description>
    <PackageTags>themia;dapper;sqlkata;postgres;npgsql;data</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Npgsql" />
    <PackageReference Include="SqlKata" />
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

- [ ] **Step 3:** Create both packages' `PublicAPI.Shipped.txt` (empty) and `PublicAPI.Unshipped.txt`
  (single line `#nullable enable`).
- [ ] **Step 4: Add both to the solution.**

Run:
```bash
dotnet sln Themia.sln add src/framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj src/framework/Themia.Framework.Data.Dapper.PostgreSql/Themia.Framework.Data.Dapper.PostgreSql.csproj
```
Expected: two `Project ... added` lines.

- [ ] **Step 5: Build both.**

Run: `dotnet build src/framework/Themia.Framework.Data.Dapper.PostgreSql/Themia.Framework.Data.Dapper.PostgreSql.csproj -c Release`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (builds the core transitively).

- [ ] **Step 6: Commit.**

```bash
git add src/framework/Themia.Framework.Data.Dapper src/framework/Themia.Framework.Data.Dapper.PostgreSql Themia.sln
git commit -m "chore(data): scaffold Themia.Framework.Data.Dapper(.PostgreSql) projects"
```

### Task 0.3: Create the test projects

**Files:**
- Create: `tests/Themia.Framework.Data.Abstractions.Tests/*.csproj`
- Create: `tests/Themia.Framework.Data.Dapper.Tests/*.csproj`
- Create: `tests/Themia.Framework.Data.Dapper.Conformance/*.csproj` (a library, not a test runner)
- Create: `tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/*.csproj`

- [ ] **Step 1: Abstractions.Tests csproj** (unit test runner):

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
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Dapper.Tests csproj** — same as Step 1 but `ProjectReference` →
  `../../src/framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj`.

- [ ] **Step 3: Conformance csproj** — a **class library** (no test SDK; it only defines abstract
  `[Fact]`-bearing base classes that integration projects subclass). It references xunit so `[Fact]` resolves:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: PostgreSql.IntegrationTests csproj:**

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
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Npgsql" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Framework.Data.Dapper.Conformance/Themia.Framework.Data.Dapper.Conformance.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper.PostgreSql/Themia.Framework.Data.Dapper.PostgreSql.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj" />
    <ProjectReference Include="../../src/framework/Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5:** Add all four to the solution (`dotnet sln Themia.sln add <each csproj>`), build the solution
  (`dotnet build Themia.sln -c Release` → 0 warnings), commit:

```bash
git add tests/Themia.Framework.Data.Abstractions.Tests tests/Themia.Framework.Data.Dapper.Tests tests/Themia.Framework.Data.Dapper.Conformance tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests Themia.sln
git commit -m "chore(data): scaffold data-layer test projects"
```

---

## PHASE 1 — Abstractions (`Themia.Framework.Data.Abstractions`)

All public types in this phase must be appended to `PublicAPI.Unshipped.txt` (exact lines given per task).
Namespace root: `Themia.Framework.Data.Abstractions`.

### Task 1.1: `PagedResult<T>`

**Files:**
- Create: `src/framework/Themia.Framework.Data.Abstractions/Paging/PagedResult.cs`
- Modify: `PublicAPI.Unshipped.txt`

- [ ] **Step 1: Implement** (a plain immutable record — trivial enough to skip a dedicated test; covered by
  repo tests later):

```csharp
namespace Themia.Framework.Data.Abstractions.Paging;

/// <summary>A page of results plus the total count of matching rows (ignoring paging).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int? Skip, int? Take);
```

- [ ] **Step 2: Append PublicAPI lines:**

```
Themia.Framework.Data.Abstractions.Paging.PagedResult<T>
Themia.Framework.Data.Abstractions.Paging.PagedResult<T>.PagedResult(System.Collections.Generic.IReadOnlyList<T>! Items, long Total, int? Skip, int? Take) -> void
Themia.Framework.Data.Abstractions.Paging.PagedResult<T>.Items.get -> System.Collections.Generic.IReadOnlyList<T>!
Themia.Framework.Data.Abstractions.Paging.PagedResult<T>.Total.get -> long
Themia.Framework.Data.Abstractions.Paging.PagedResult<T>.Skip.get -> int?
Themia.Framework.Data.Abstractions.Paging.PagedResult<T>.Take.get -> int?
```

> Note: PublicAPI records also need the compiler-generated `Equals`/`GetHashCode`/`<Clone>$`/`Deconstruct`/
> `ToString`/`EqualityContract` lines. After writing each public type, run the build once; `RS0016` errors
> list the **exact** missing signatures — paste them in. (This is the canonical workflow; do it for every
> public type below rather than hand-guessing record members.)

- [ ] **Step 3: Build** `dotnet build src/framework/Themia.Framework.Data.Abstractions -c Release`, paste any
  `RS0016`-reported lines into `PublicAPI.Unshipped.txt`, rebuild → 0 warnings.
- [ ] **Step 4: Commit** `git commit -am "feat(data): PagedResult<T>"`.

### Task 1.2: `ISpecification<T>` + `OrderExpression<T>`

**Files:**
- Create: `src/framework/Themia.Framework.Data.Abstractions/Specifications/ISpecification.cs`
- Modify: `PublicAPI.Unshipped.txt`

- [ ] **Step 1: Implement.**

```csharp
using System.Linq.Expressions;

namespace Themia.Framework.Data.Abstractions.Specifications;

/// <summary>One ORDER BY term: a member selector and its direction.</summary>
public sealed record OrderExpression<T>(Expression<Func<T, object?>> KeySelector, bool Descending);

/// <summary>
/// A provider-agnostic query specification: an optional filter predicate, ordering, paging, and an
/// explicit opt-out of the tenant filter. Translated to LINQ by the EF layer and to SqlKata by the
/// Dapper layer. Single-entity predicates only; joins/projections are provider-native (tier 2).
/// </summary>
public interface ISpecification<T>
{
    Expression<Func<T, bool>>? Criteria { get; }
    IReadOnlyList<OrderExpression<T>> OrderBy { get; }
    int? Skip { get; }
    int? Take { get; }

    /// <summary>When true, the tenant predicate is NOT applied (deliberate cross-tenant/admin access).</summary>
    bool IgnoreTenantFilter { get; }
}
```

- [ ] **Step 2:** Append the PublicAPI lines (interface members + the `OrderExpression<T>` record; use the
  build-then-paste workflow from Task 1.1 Step 2).
- [ ] **Step 3:** Build → 0 warnings.
- [ ] **Step 4: Commit** `git commit -am "feat(data): ISpecification<T> + OrderExpression<T>"`.

### Task 1.3: `Specification<T>` base (fluent) + combinator tests

**Files:**
- Create: `src/framework/Themia.Framework.Data.Abstractions/Specifications/Specification.cs`
- Create: `src/framework/Themia.Framework.Data.Abstractions/Specifications/SpecificationExtensions.cs`
- Test: `tests/Themia.Framework.Data.Abstractions.Tests/SpecificationTests.cs`
- Modify: `PublicAPI.Unshipped.txt`

- [ ] **Step 1: Write the failing test** (combinators + fluent builder behaviour, compiled against
  in-memory data so it needs no DB):

```csharp
using System.Linq.Expressions;
using Themia.Framework.Data.Abstractions.Specifications;
using Xunit;

namespace Themia.Framework.Data.Abstractions.Tests;

public sealed class SpecificationTests
{
    private sealed record Person(string Name, int Age);

    private sealed class AdultSpec : Specification<Person>
    {
        public AdultSpec() { Where(p => p.Age >= 18); OrderBy(p => p.Name); }
    }

    [Fact]
    public void Where_SetsCriteria_ThatCompilesAndFilters()
    {
        var spec = new AdultSpec();
        var predicate = spec.Criteria!.Compile();
        Assert.True(predicate(new Person("A", 20)));
        Assert.False(predicate(new Person("B", 10)));
    }

    [Fact]
    public void And_CombinesTwoCriteria_WithLogicalAnd()
    {
        ISpecification<Person> spec = new AdultSpec().And(p => p.Name.StartsWith("A"));
        var predicate = spec.Criteria!.Compile();
        Assert.True(predicate(new Person("Ann", 30)));
        Assert.False(predicate(new Person("Bob", 30)));   // fails name
        Assert.False(predicate(new Person("Amy", 10)));   // fails age
    }

    [Fact]
    public void Or_CombinesTwoCriteria_WithLogicalOr()
    {
        ISpecification<Person> spec = new AdultSpec().Or(p => p.Name == "Kid");
        var predicate = spec.Criteria!.Compile();
        Assert.True(predicate(new Person("Kid", 5)));      // matches the Or branch
        Assert.True(predicate(new Person("Zed", 40)));     // matches the adult branch
        Assert.False(predicate(new Person("Tom", 5)));
    }

    [Fact]
    public void Not_NegatesCriteria()
    {
        ISpecification<Person> spec = new AdultSpec().Not();
        var predicate = spec.Criteria!.Compile();
        Assert.True(predicate(new Person("Kid", 5)));
        Assert.False(predicate(new Person("Adult", 30)));
    }

    [Fact]
    public void Page_SetsSkipAndTake()
    {
        var spec = new AdultSpec();
        ((Specification<Person>)spec).Page(skip: 10, take: 5);
        Assert.Equal(10, spec.Skip);
        Assert.Equal(5, spec.Take);
    }
}
```

- [ ] **Step 2: Run → fails to compile** (`Specification<T>`/`And`/`Or`/`Not`/`Page` not defined).

Run: `dotnet test tests/Themia.Framework.Data.Abstractions.Tests --filter SpecificationTests`
Expected: build failure (types undefined).

- [ ] **Step 3: Implement `Specification<T>`:**

```csharp
using System.Linq.Expressions;

namespace Themia.Framework.Data.Abstractions.Specifications;

/// <summary>Fluent base for building an <see cref="ISpecification{T}"/>.</summary>
public abstract class Specification<T> : ISpecification<T>
{
    private readonly List<OrderExpression<T>> orderBy = [];

    public Expression<Func<T, bool>>? Criteria { get; private set; }
    public IReadOnlyList<OrderExpression<T>> OrderBy => orderBy;
    public int? Skip { get; private set; }
    public int? Take { get; private set; }
    public bool IgnoreTenantFilter { get; private set; }

    protected Specification<T> Where(Expression<Func<T, bool>> criteria)
    {
        Criteria = Criteria is null ? criteria : Criteria.AndAlso(criteria);
        return this;
    }

    protected Specification<T> AddOrderBy(Expression<Func<T, object?>> keySelector, bool descending = false)
    {
        orderBy.Add(new OrderExpression<T>(keySelector, descending));
        return this;
    }

    // Convenience aliases used by callers and tests.
    public Specification<T> OrderBy(Expression<Func<T, object?>> keySelector) => AddOrderBy(keySelector, false);
    public Specification<T> OrderByDescending(Expression<Func<T, object?>> keySelector) => AddOrderBy(keySelector, true);

    public Specification<T> Page(int? skip, int? take)
    {
        Skip = skip;
        Take = take;
        return this;
    }

    public Specification<T> WithoutTenantFilter()
    {
        IgnoreTenantFilter = true;
        return this;
    }
}
```

- [ ] **Step 4: Implement combinators + the parameter-rebinding visitor** in `SpecificationExtensions.cs`:

```csharp
using System.Linq.Expressions;

namespace Themia.Framework.Data.Abstractions.Specifications;

/// <summary>Combinators that produce a new specification from an existing one + an extra predicate.</summary>
public static class SpecificationExtensions
{
    public static ISpecification<T> And<T>(this ISpecification<T> spec, Expression<Func<T, bool>> extra) =>
        new CombinedSpecification<T>(spec, spec.Criteria is null ? extra : spec.Criteria.AndAlso(extra));

    public static ISpecification<T> Or<T>(this ISpecification<T> spec, Expression<Func<T, bool>> extra) =>
        new CombinedSpecification<T>(spec, spec.Criteria is null ? extra : spec.Criteria.OrElse(extra));

    public static ISpecification<T> Not<T>(this ISpecification<T> spec)
    {
        if (spec.Criteria is null)
            return spec;
        var param = spec.Criteria.Parameters[0];
        var negated = Expression.Lambda<Func<T, bool>>(Expression.Not(spec.Criteria.Body), param);
        return new CombinedSpecification<T>(spec, negated);
    }

    internal static Expression<Func<T, bool>> AndAlso<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
        => Combine(left, right, Expression.AndAlso);

    internal static Expression<Func<T, bool>> OrElse<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
        => Combine(left, right, Expression.OrElse);

    private static Expression<Func<T, bool>> Combine<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right,
        Func<Expression, Expression, BinaryExpression> op)
    {
        var param = Expression.Parameter(typeof(T), "x");
        var l = new ReplaceParameterVisitor(left.Parameters[0], param).Visit(left.Body)!;
        var r = new ReplaceParameterVisitor(right.Parameters[0], param).Visit(right.Body)!;
        return Expression.Lambda<Func<T, bool>>(op(l, r), param);
    }

    private sealed class ReplaceParameterVisitor(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}

// Wraps an existing spec but overrides its Criteria; preserves OrderBy/paging/tenant flag.
internal sealed class CombinedSpecification<T> : ISpecification<T>
{
    private readonly ISpecification<T> inner;
    public CombinedSpecification(ISpecification<T> inner, Expression<Func<T, bool>> criteria)
    {
        this.inner = inner;
        Criteria = criteria;
    }
    public Expression<Func<T, bool>>? Criteria { get; }
    public IReadOnlyList<OrderExpression<T>> OrderBy => inner.OrderBy;
    public int? Skip => inner.Skip;
    public int? Take => inner.Take;
    public bool IgnoreTenantFilter => inner.IgnoreTenantFilter;
}
```

- [ ] **Step 5: Run the tests → pass.**

Run: `dotnet test tests/Themia.Framework.Data.Abstractions.Tests --filter SpecificationTests`
Expected: `Passed! - Failed: 0, Passed: 5`.

- [ ] **Step 6:** Append PublicAPI lines for `Specification<T>`, `SpecificationExtensions` (build-then-paste);
  `CombinedSpecification`/`ReplaceParameterVisitor` are `internal` (no PublicAPI). Build → 0 warnings.
- [ ] **Step 7: Commit** `git commit -am "feat(data): Specification<T> base + And/Or/Not combinators"`.

### Task 1.4: Repository + UoW + supporting contracts

**Files:**
- Create: `src/framework/Themia.Framework.Data.Abstractions/Repositories/IReadRepository.cs`
- Create: `src/framework/Themia.Framework.Data.Abstractions/Repositories/IRepository.cs`
- Create: `src/framework/Themia.Framework.Data.Abstractions/UnitOfWork/ITransactionScope.cs`
- Create: `src/framework/Themia.Framework.Data.Abstractions/UnitOfWork/IUnitOfWork.cs`
- Create: `src/framework/Themia.Framework.Data.Abstractions/Filtering/IDataFilterScope.cs`
- Create: `src/framework/Themia.Framework.Data.Abstractions/Auditing/ICurrentUserAccessor.cs`
- Create: `src/framework/Themia.Framework.Data.Abstractions/Exceptions/UnsupportedSpecificationException.cs`
- Modify: `PublicAPI.Unshipped.txt`

These are pure contracts (no logic) — implemented + behaviourally tested in Phases 2/4. Define them now so
both implementations compile against a stable surface.

- [ ] **Step 1: Implement all contracts.**

```csharp
// Repositories/IReadRepository.cs
using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Specifications;
namespace Themia.Framework.Data.Abstractions.Repositories;

public interface IReadRepository<T, in TKey> where T : class
{
    Task<T?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> ListAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
    Task<T?> FirstOrDefaultAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
    Task<long> CountAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
    Task<PagedResult<T>> PageAsync(ISpecification<T> specification, CancellationToken cancellationToken = default);
}
```

```csharp
// Repositories/IRepository.cs
namespace Themia.Framework.Data.Abstractions.Repositories;

public interface IRepository<T, in TKey> : IReadRepository<T, TKey> where T : class
{
    Task AddAsync(T entity, CancellationToken cancellationToken = default);
    void Update(T entity);
    void Remove(T entity);   // soft-delete when T : ISoftDeletable, else hard delete
}
```

```csharp
// UnitOfWork/ITransactionScope.cs
namespace Themia.Framework.Data.Abstractions.UnitOfWork;

public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
```

```csharp
// UnitOfWork/IUnitOfWork.cs
namespace Themia.Framework.Data.Abstractions.UnitOfWork;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default);
}
```

```csharp
// Filtering/IDataFilterScope.cs
namespace Themia.Framework.Data.Abstractions.Filtering;

/// <summary>
/// Deliberate, explicit opt-out of the tenant filter for the duration of the returned scope.
/// Honoured identically by the EF adapter (IgnoreQueryFilters) and the Dapper tenant-seeded factory.
/// Audit and soft-delete are unaffected.
/// </summary>
public interface IDataFilterScope
{
    IDisposable BypassTenantFilter();
    bool IsTenantFilterBypassed { get; }
}
```

```csharp
// Auditing/ICurrentUserAccessor.cs
namespace Themia.Framework.Data.Abstractions.Auditing;

/// <summary>Supplies the identifier stamped into CreatedBy/LastModifiedBy/DeletedBy. Default impls return null.</summary>
public interface ICurrentUserAccessor
{
    string? UserId { get; }
}
```

```csharp
// Exceptions/UnsupportedSpecificationException.cs
namespace Themia.Framework.Data.Abstractions.Exceptions;

/// <summary>
/// Thrown when a specification's expression tree uses a construct the Dapper translator does not support
/// (e.g. nested navigation, joins, projections, or an unsupported method). Drop to provider-native
/// (tier-2 SqlKata) for such queries.
/// </summary>
public sealed class UnsupportedSpecificationException : Exception
{
    public UnsupportedSpecificationException(string message) : base(message) { }
}
```

- [ ] **Step 2:** Append PublicAPI lines for all seven types (build-then-paste). Build → 0 warnings.
- [ ] **Step 3: Commit** `git commit -am "feat(data): repository, unit-of-work, filter-scope, current-user, exception contracts"`.

---

## PHASE 2 — Dapper core (`Themia.Framework.Data.Dapper`)

Namespace root: `Themia.Framework.Data.Dapper`. Most types here are `internal` (used via DI), so they need
**no** PublicAPI entries — only the DI extension + `ITenantQueryFactory` + `NullCurrentUserAccessor` are
public (`DataFilterScope` is public but lives in Abstractions). Unit tests live in
`Themia.Framework.Data.Dapper.Tests` (has InternalsVisibleTo).

> **Build-order note:** Tasks 2.1–2.4 are independently unit-tested and build green on their own. Tasks
> 2.5–2.10 (connection context → query factory → repositories → UoW → options/DI) form an **interdependent
> cluster**: e.g. `TenantQueryFactory` (2.6) references `DapperDataOptions` defined in 2.10. They compile as a
> unit. Expect the Dapper-core project to build green at the end of **Task 2.10**, not after every intermediate
> task; commit each task's files regardless (the cluster lands together). The unit-tested tasks (2.1, 2.3, 2.4)
> must still go green when run.

### Task 2.1: Entity mapping (conventions + cached key setter)

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/Mapping/EntityMapping.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper/Mapping/EntityMappingRegistry.cs`
- Test: `tests/Themia.Framework.Data.Dapper.Tests/EntityMappingTests.cs`

- [ ] **Step 1: Write the failing test:**

```csharp
using Themia.Framework.Data.Dapper.Mapping;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

public sealed class EntityMappingTests
{
    private sealed class AssetCategory { public int Id { get; set; } public string? DisplayName { get; set; } }

    [Fact]
    public void Convention_PluralizesTable_AndSnakeCasesColumns()
    {
        var map = EntityMapping.ForConvention<AssetCategory>();
        Assert.Equal("asset_categories", map.Table);
        Assert.Equal("id", map.KeyColumn);
        Assert.Equal("display_name", map.Column(nameof(AssetCategory.DisplayName)));
    }

    [Fact]
    public void ToSnakeCase_HandlesAcronymsAndDigits()
    {
        Assert.Equal("created_at", EntityMapping.ToSnakeCase("CreatedAt"));
        Assert.Equal("tenant_id", EntityMapping.ToSnakeCase("TenantId"));
        Assert.Equal("html_url", EntityMapping.ToSnakeCase("HtmlUrl"));   // sequential capitals
    }
}
```

- [ ] **Step 2: Run → fails** (`EntityMapping` undefined).

Run: `dotnet test tests/Themia.Framework.Data.Dapper.Tests --filter EntityMappingTests` → build failure.

- [ ] **Step 3: Implement `EntityMapping`** (snake_case + naive pluralization matching EFCore.NamingConventions'
  default snake_case; pluralization is the simple "+s / +es / y→ies" rule — document that an override is
  available for irregulars):

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Themia.Framework.Data.Dapper.Mapping;

/// <summary>Table/column name mapping for an entity type. Convention = snake_case columns, snake_case
/// pluralized table; the key property is "Id". Use <see cref="EntityMappingRegistry"/> to override.</summary>
public sealed class EntityMapping
{
    private readonly Dictionary<string, string> columnByProperty;

    private EntityMapping(string table, string keyColumn, string keyProperty, Dictionary<string, string> columns, Action<object, object?> keySetter, Type keyType)
    {
        Table = table; KeyColumn = keyColumn; KeyProperty = keyProperty;
        columnByProperty = columns; KeySetter = keySetter; KeyType = keyType;
    }

    public string Table { get; }
    public string KeyColumn { get; }
    public string KeyProperty { get; }
    public Type KeyType { get; }
    /// <summary>Writes the (possibly protected) key property — used to populate store-generated keys.</summary>
    public Action<object, object?> KeySetter { get; }

    public string Column(string propertyName) => columnByProperty[propertyName];
    public IReadOnlyDictionary<string, string> Columns => columnByProperty;

    public static EntityMapping ForConvention<T>() => ForConvention(typeof(T));

    public static EntityMapping ForConvention(Type type)
    {
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                        && p.Name != "DomainEvents" && p.Name != "IsTransient")
            .ToArray();

        var columns = props.ToDictionary(p => p.Name, p => ToSnakeCase(p.Name));
        var key = props.FirstOrDefault(p => p.Name == "Id")
                  ?? throw new InvalidOperationException($"Entity '{type.Name}' has no 'Id' property; provide an EntityMapping override.");
        var setter = BuildSetter(key);
        return new EntityMapping(Pluralize(ToSnakeCase(type.Name)), ToSnakeCase(key.Name), key.Name, columns, setter, key.PropertyType);
    }

    // Compiled setter that can set a protected/private setter (Entity<TId>.Id is { get; protected set; }).
    private static Action<object, object?> BuildSetter(PropertyInfo key)
    {
        var setMethod = key.GetSetMethod(nonPublic: true)
                        ?? throw new InvalidOperationException($"Key property '{key.DeclaringType?.Name}.{key.Name}' has no setter.");
        return (entity, value) => setMethod.Invoke(entity, [value]);
    }

    public static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) ||
                              (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Pluralize(string snake)
    {
        if (snake.EndsWith('y') && snake.Length > 1 && !"aeiou".Contains(snake[^2]))
            return string.Concat(snake.AsSpan(0, snake.Length - 1), "ies");
        if (snake.EndsWith('s') || snake.EndsWith("ch") || snake.EndsWith("sh") || snake.EndsWith('x'))
            return snake + "es";
        return snake + "s";
    }
}
```

> Reflection note (repo rule): the only reflection is at mapping-build time, cached per type in the registry
> (Task 2.1 Step 5) — never in a per-row hot path. The compiled `KeySetter` is invoked only once per inserted
> entity. This is framework-level metadata, the sanctioned exception in the .NET rules.

- [ ] **Step 4: Implement `EntityMappingRegistry`** (caches `EntityMapping` per type; allows overrides):

```csharp
using System.Collections.Concurrent;

namespace Themia.Framework.Data.Dapper.Mapping;

/// <summary>Caches one <see cref="EntityMapping"/> per entity type. Register overrides at startup.</summary>
public sealed class EntityMappingRegistry
{
    private readonly ConcurrentDictionary<Type, EntityMapping> cache = new();
    private readonly Dictionary<Type, EntityMapping> overrides = new();

    public void Register<T>(EntityMapping mapping) => overrides[typeof(T)] = mapping;

    public EntityMapping For<T>() => cache.GetOrAdd(typeof(T),
        t => overrides.TryGetValue(t, out var m) ? m : EntityMapping.ForConvention(t));
}
```

- [ ] **Step 5: Set Dapper underscore matching once.** Add a static initializer the DI extension calls
  (Task 2.10). For now add `Mapping/DapperConfiguration.cs`:

```csharp
namespace Themia.Framework.Data.Dapper.Mapping;

internal static class DapperConfiguration
{
    private static bool configured;
    public static void EnsureConfigured()
    {
        if (configured) return;
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;   // tenant_id -> TenantId
        configured = true;
    }
}
```

- [ ] **Step 6: Run the tests → pass.** `dotnet test tests/Themia.Framework.Data.Dapper.Tests --filter EntityMappingTests` → `Passed! ... Passed: 2`.
- [ ] **Step 7:** Append PublicAPI lines for the public `EntityMapping` + `EntityMappingRegistry`. Build → 0 warnings.
- [ ] **Step 8: Commit** `git commit -am "feat(dapper): entity mapping conventions + cached key setter"`.

### Task 2.2: `ISqlCompiler` seam + `CompiledSql`

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/Sql/CompiledSql.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper/Sql/ISqlCompiler.cs`

- [ ] **Step 1: Implement** (no test yet — exercised via the PostgreSql compiler in Task 3.1 and the
  translator tests in Task 2.3 which use a test-double compiler):

```csharp
namespace Themia.Framework.Data.Dapper.Sql;

/// <summary>A compiled SQL statement and its named parameter values (ready for Dapper).</summary>
public sealed record CompiledSql(string Sql, IReadOnlyDictionary<string, object?> Parameters);
```

```csharp
using SqlKata;
namespace Themia.Framework.Data.Dapper.Sql;

/// <summary>Compiles a SqlKata <see cref="Query"/> to engine-specific SQL + parameters. Implemented per
/// engine (PostgresCompiler, etc.). The only place SqlKata's compiler types are referenced.</summary>
public interface ISqlCompiler
{
    CompiledSql Compile(Query query);
}
```

- [ ] **Step 2:** Append PublicAPI lines (`CompiledSql`, `ISqlCompiler`). Build → 0 warnings.
- [ ] **Step 3: Commit** `git commit -am "feat(dapper): ISqlCompiler seam + CompiledSql"`.

### Task 2.3: Specification → SqlKata translator (TDD, the hard piece)

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/Translation/SpecificationTranslator.cs`
- Test: `tests/Themia.Framework.Data.Dapper.Tests/SpecificationTranslatorTests.cs`

The translator mutates a SqlKata `Query` (already pointed at the table) by walking `ISpecification.Criteria`
and adding `Where*` clauses, then applies ordering + Skip/Take. Tests compile each translated query with a
real `PostgresCompiler` (SqlKata) and assert on the SQL + bindings — this proves both the translator and
the SqlKata integration without a database.

- [ ] **Step 1: Write failing tests** (one per supported shape + the unsupported-throws case):

```csharp
using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Translation;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

public sealed class SpecificationTranslatorTests
{
    private sealed class Asset
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Status { get; set; }
        public int Quantity { get; set; }
    }

    private sealed class TestSpec : Specification<Asset> { }   // exposes Where/OrderBy/Page as protected — use a subclass per test via builder
    private static Specification<Asset> Spec(Action<SpecBuilder> build) { var b = new SpecBuilder(); build(b); return b; }

    // Minimal builder exposing the protected fluent API for tests.
    private sealed class SpecBuilder : Specification<Asset>
    {
        public new SpecBuilder Where(System.Linq.Expressions.Expression<Func<Asset, bool>> c) { base.Where(c); return this; }
    }

    private static readonly EntityMapping Map = EntityMapping.ForConvention<Asset>();
    private static (string sql, IReadOnlyDictionary<string, object?> p) Compile(ISpecification<Asset> spec)
    {
        var query = new Query(Map.Table);
        SpecificationTranslator.Apply(query, spec, Map);
        var result = new PostgresCompiler().Compile(query);
        return (result.Sql, result.NamedBindings);
    }

    [Fact]
    public void Equality_OnInt_EmitsParameterizedWhere()
    {
        var (sql, p) = Compile(Spec(b => b.Where(a => a.Quantity == 5)));
        Assert.Contains("\"quantity\" = @", sql);
        Assert.Contains(5, p.Values);
    }

    [Fact]
    public void StringContains_EmitsLike()
    {
        var (sql, p) = Compile(Spec(b => b.Where(a => a.Name.Contains("rig"))));
        Assert.Contains("lower(\"name\") like ?", sql.ToLowerInvariant().Replace("@p0", "?"));
        Assert.Contains("%rig%", p.Values);
    }

    [Fact]
    public void NullCheck_EmitsIsNull()
    {
        var (sql, _) = Compile(Spec(b => b.Where(a => a.Status == null)));
        Assert.Contains("\"status\" is null", sql.ToLowerInvariant());
    }

    [Fact]
    public void CollectionContains_EmitsIn()
    {
        var ids = new[] { 1, 2, 3 };
        var (sql, _) = Compile(Spec(b => b.Where(a => ids.Contains(a.Id))));
        Assert.Contains("\"id\" in (", sql.ToLowerInvariant());
    }

    [Fact]
    public void AndOr_Nest_Correctly()
    {
        var (sql, _) = Compile(Spec(b => b.Where(a => a.Quantity > 0 && (a.Status == "x" || a.Name == "y"))));
        Assert.Contains("and", sql.ToLowerInvariant());
        Assert.Contains("or", sql.ToLowerInvariant());
    }

    [Fact]
    public void UnsupportedConstruct_Throws()
    {
        // Method the translator does not support (e.g. ToString on a member) -> fail fast.
        Assert.Throws<UnsupportedSpecificationException>(() =>
            Compile(Spec(b => b.Where(a => a.Quantity.ToString() == "5"))));
    }
}
```

- [ ] **Step 2: Run → fails** (`SpecificationTranslator` undefined).

Run: `dotnet test tests/Themia.Framework.Data.Dapper.Tests --filter SpecificationTranslatorTests` → build failure.

- [ ] **Step 3: Implement the translator.**

```csharp
using System.Collections;
using System.Linq.Expressions;
using SqlKata;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Framework.Data.Dapper.Mapping;

namespace Themia.Framework.Data.Dapper.Translation;

/// <summary>Translates an <see cref="ISpecification{T}"/> onto a SqlKata <see cref="Query"/>: criteria →
/// Where clauses, ordering → OrderBy, Skip/Take → Offset/Limit. Single-table predicates only.</summary>
internal static class SpecificationTranslator
{
    public static void Apply<T>(Query query, ISpecification<T> spec, EntityMapping map)
    {
        if (spec.Criteria is not null)
            query.Where(q => Translate(q, spec.Criteria.Body, spec.Criteria.Parameters[0], map));

        foreach (var order in spec.OrderBy)
        {
            var column = ColumnOf(order.KeySelector.Body, order.KeySelector.Parameters[0], map);
            if (order.Descending) query.OrderByDesc(column); else query.OrderBy(column);
        }

        if (spec.Skip is { } skip) query.Offset(skip);
        if (spec.Take is { } take) query.Limit(take);
    }

    private static Query Translate(Query q, Expression expr, ParameterExpression root, EntityMapping map)
    {
        switch (expr)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } b:
                return q.Where(inner => Translate(inner, b.Left, root, map))
                        .Where(inner => Translate(inner, b.Right, root, map));
            case BinaryExpression { NodeType: ExpressionType.OrElse } b:
                return q.Where(inner => Translate(inner, b.Left, root, map))
                        .OrWhere(inner => Translate(inner, b.Right, root, map));
            case UnaryExpression { NodeType: ExpressionType.Not } u:
                return q.WhereNot(inner => Translate(inner, u.Operand, root, map));
            case BinaryExpression b:
                return Comparison(q, b, root, map);
            case MethodCallExpression m:
                return MethodCall(q, m, root, map);
            case MemberExpression { Type.Name: nameof(Boolean) } member:   // bool property as predicate
                return q.Where(ColumnOf(member, root, map), true);
            default:
                throw new UnsupportedSpecificationException(
                    $"Unsupported predicate '{expr}'. Use provider-native (tier-2) SqlKata for this query.");
        }
    }

    private static Query Comparison(Query q, BinaryExpression b, ParameterExpression root, EntityMapping map)
    {
        // Normalize so the column is on the left.
        var (memberSide, valueSide, op) = Orient(b, root);
        var column = ColumnOf(memberSide, root, map);
        var value = Evaluate(valueSide);

        if (value is null)
            return op is ExpressionType.Equal ? q.WhereNull(column)
                 : op is ExpressionType.NotEqual ? q.WhereNotNull(column)
                 : throw new UnsupportedSpecificationException($"Only ==/!= null is supported for '{column}'.");

        return op switch
        {
            ExpressionType.Equal => q.Where(column, value),
            ExpressionType.NotEqual => q.WhereNot(column, value),
            ExpressionType.GreaterThan => q.Where(column, ">", value),
            ExpressionType.GreaterThanOrEqual => q.Where(column, ">=", value),
            ExpressionType.LessThan => q.Where(column, "<", value),
            ExpressionType.LessThanOrEqual => q.Where(column, "<=", value),
            _ => throw new UnsupportedSpecificationException($"Unsupported operator '{op}'.")
        };
    }

    private static (Expression member, Expression value, ExpressionType op) Orient(BinaryExpression b, ParameterExpression root)
    {
        if (RefersTo(b.Left, root)) return (b.Left, b.Right, b.NodeType);
        if (RefersTo(b.Right, root)) return (b.Right, b.Left, Flip(b.NodeType));
        throw new UnsupportedSpecificationException($"Comparison '{b}' does not reference the entity.");
    }

    private static Query MethodCall(Query q, MethodCallExpression m, ParameterExpression root, EntityMapping map)
    {
        // string.Contains/StartsWith/EndsWith  ->  LIKE
        if (m.Object is not null && RefersTo(m.Object, root) && m.Method.DeclaringType == typeof(string))
        {
            var column = ColumnOf(m.Object, root, map);
            var arg = Evaluate(m.Arguments[0])?.ToString() ?? "";
            return m.Method.Name switch
            {
                nameof(string.Contains) => q.WhereLike(column, $"%{arg}%"),
                nameof(string.StartsWith) => q.WhereLike(column, $"{arg}%"),
                nameof(string.EndsWith) => q.WhereLike(column, $"%{arg}"),
                _ => throw new UnsupportedSpecificationException($"Unsupported string method '{m.Method.Name}'.")
            };
        }

        // collection.Contains(x.Member)  ->  WHERE column IN (...)
        if (m.Method.Name == nameof(Enumerable.Contains))
        {
            var (collectionExpr, memberExpr) = m.Object is null
                ? (m.Arguments[0], m.Arguments[1])   // Enumerable.Contains(source, item)
                : (m.Object, m.Arguments[0]);          // List<T>.Contains(item)
            if (RefersTo(memberExpr, root) && !RefersTo(collectionExpr, root))
            {
                var column = ColumnOf(memberExpr, root, map);
                var values = ((IEnumerable)Evaluate(collectionExpr)!).Cast<object?>().ToArray();
                return q.WhereIn(column, values);
            }
        }

        throw new UnsupportedSpecificationException(
            $"Unsupported method call '{m.Method.Name}'. Use provider-native (tier-2) SqlKata.");
    }

    private static string ColumnOf(Expression expr, ParameterExpression root, EntityMapping map)
    {
        var e = Unwrap(expr);
        if (e is MemberExpression { Expression: ParameterExpression p } member && p == root)
            return map.Column(member.Member.Name);
        throw new UnsupportedSpecificationException(
            $"Only direct properties of the entity are supported in specifications ('{expr}'). Use tier-2 for joins/navigation.");
    }

    private static Expression Unwrap(Expression e) =>
        e is UnaryExpression { NodeType: ExpressionType.Convert } u ? u.Operand : e;

    private static bool RefersTo(Expression expr, ParameterExpression root)
    {
        var found = false;
        new ParameterFinder(root, () => found = true).Visit(expr);
        return found;
    }

    private static object? Evaluate(Expression expr)
    {
        if (Unwrap(expr) is ConstantExpression c) return c.Value;
        // Compile-and-invoke captured variables / member access on closures -> a parameter value.
        return Expression.Lambda(Unwrap(expr)).Compile().DynamicInvoke();
    }

    private static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => t
    };

    private sealed class ParameterFinder(ParameterExpression target, Action onFound) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == target) onFound();
            return base.VisitParameter(node);
        }
    }
}
```

- [ ] **Step 4: Run the tests → pass** (adjust the SQL substring assertions in Step 1 to the exact strings the
  `PostgresCompiler` emits — run once, read the actual SQL from a failing assert, pin it; the translator logic
  itself is what's under test, the exact quoting is SqlKata's).

Run: `dotnet test tests/Themia.Framework.Data.Dapper.Tests --filter SpecificationTranslatorTests`
Expected: all green (after pinning the compiler's exact output).

- [ ] **Step 5: Commit** `git commit -am "feat(dapper): specification -> SqlKata translator (supported subset + fail-fast)"`.

### Task 2.4: Tenant predicate + soft-delete seeding

**Files:**
- Create: `src/framework/Themia.Framework.Data.Abstractions/Filtering/DataFilterScope.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper/Tenancy/TenantPredicate.cs`
- Test: `tests/Themia.Framework.Data.Dapper.Tests/TenantPredicateTests.cs`
- Modify: Abstractions `PublicAPI.Unshipped.txt`

`DataFilterScope` is the public `IDataFilterScope` impl backed by an `AsyncLocal<bool>`. **It lives in
`Themia.Framework.Data.Abstractions`** (pure `AsyncLocal`, no Dapper/EF types) so BOTH layers share the one
carrier — the EF adapter and the Dapper factory read the same scope. `TenantPredicate` (Dapper-only) applies
the tenant + soft-delete clauses to a SqlKata query, honouring `IgnoreTenantFilter`, the bypass scope, the
current `TenantId`, and the global-records rule.

- [ ] **Step 1: Implement `DataFilterScope` in the Abstractions package:**

```csharp
namespace Themia.Framework.Data.Abstractions.Filtering;

/// <summary>AsyncLocal-backed tenant-filter bypass. Shared carrier read by the Dapper factory and the EF adapter.</summary>
public sealed class DataFilterScope : IDataFilterScope
{
    private static readonly AsyncLocal<bool> Bypassed = new();
    public bool IsTenantFilterBypassed => Bypassed.Value;

    public IDisposable BypassTenantFilter()
    {
        var previous = Bypassed.Value;
        Bypassed.Value = true;
        return new Restore(() => Bypassed.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }
}
```

- [ ] **Step 2: Write the failing test for `TenantPredicate`:**

```csharp
using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Tenancy;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

public sealed class TenantPredicateTests
{
    private sealed class Doc : Themia.Framework.Core.Abstractions.Tenancy.ITenantEntity,
                               Themia.Framework.Core.Abstractions.Entities.ISoftDeletable
    {
        public int Id { get; set; }
        public TenantId? TenantId { get; set; }
        public bool IsDeleted { get; }
        public System.DateTimeOffset? DeletedAt { get; }
        public string? DeletedBy { get; }
        public System.DateTimeOffset? RestoredAt { get; }
        public string? RestoredBy { get; }
    }

    private static readonly EntityMapping Map = EntityMapping.ForConvention<Doc>();
    private static string Sql(TenantId? tenant, bool bypass)
    {
        var q = new Query(Map.Table);
        TenantPredicate.Apply<Doc>(q, tenant, includeGlobalRecords: true, bypassTenantFilter: bypass, Map);
        return new PostgresCompiler().Compile(q).Sql.ToLowerInvariant();
    }

    [Fact]
    public void WithTenant_AddsTenantAndNotDeleted()
    {
        var sql = Sql(new TenantId("acme"), bypass: false);
        Assert.Contains("tenant_id", sql);
        Assert.Contains("is_deleted", sql);
    }

    [Fact]
    public void Bypass_OmitsTenant_ButKeepsSoftDelete()
    {
        var sql = Sql(new TenantId("acme"), bypass: true);
        Assert.DoesNotContain("tenant_id", sql);
        Assert.Contains("is_deleted", sql);
    }

    [Fact]
    public void NoTenant_NotBypassed_OnlyGlobalRecords()
    {
        var sql = Sql(null, bypass: false);
        Assert.Contains("tenant_id", sql);   // tenant_id IS NULL
        Assert.Contains("is null", sql);
    }
}
```

- [ ] **Step 3: Run → fails** (`TenantPredicate` undefined).
- [ ] **Step 4: Implement `TenantPredicate`:**

```csharp
using SqlKata;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Dapper.Mapping;

namespace Themia.Framework.Data.Dapper.Tenancy;

internal static class TenantPredicate
{
    public static void Apply<T>(Query query, TenantId? tenant, bool includeGlobalRecords, bool bypassTenantFilter, EntityMapping map)
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
                query.WhereNull(column);   // no current tenant -> only global records
            }
        }

        if (typeof(ISoftDeletable).IsAssignableFrom(typeof(T)))
            query.Where(map.Column(nameof(ISoftDeletable.IsDeleted)), false);
    }
}
```

- [ ] **Step 5: Run → pass.** `dotnet test tests/Themia.Framework.Data.Dapper.Tests --filter TenantPredicateTests` → 3 passed.
- [ ] **Step 6:** Append PublicAPI for `DataFilterScope` to the **Abstractions** package's `PublicAPI.Unshipped.txt`
  (it lives there). `TenantPredicate` is `internal` (no PublicAPI). Build → 0 warnings.
- [ ] **Step 7: Commit** `git commit -am "feat(dapper): tenant + soft-delete predicate seeding + DataFilterScope"`.

### Task 2.5: Connection context + factory seam

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/Connection/IDapperConnectionFactory.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper/Connection/IDapperConnectionContext.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper/Connection/DapperConnectionContext.cs`

- [ ] **Step 1: Implement the contracts + scoped context** (no unit test — exercised by the PG integration
  suite; this is thin I/O plumbing):

```csharp
// IDapperConnectionFactory.cs
using System.Data.Common;
namespace Themia.Framework.Data.Dapper.Connection;

/// <summary>Engine seam: creates a (closed) connection. The Postgres package returns an NpgsqlConnection
/// using the tenant-resolved connection string.</summary>
public interface IDapperConnectionFactory
{
    DbConnection Create();
}
```

```csharp
// IDapperConnectionContext.cs
using System.Data.Common;
namespace Themia.Framework.Data.Dapper.Connection;

/// <summary>Scoped holder of the one connection + ambient transaction shared by repositories and the UoW.</summary>
public interface IDapperConnectionContext : IAsyncDisposable
{
    Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken);
    DbTransaction? CurrentTransaction { get; }
    Task<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    void ClearTransaction();
}
```

```csharp
// DapperConnectionContext.cs
using System.Data;
using System.Data.Common;
namespace Themia.Framework.Data.Dapper.Connection;

internal sealed class DapperConnectionContext(IDapperConnectionFactory factory) : IDapperConnectionContext
{
    private DbConnection? connection;
    public DbTransaction? CurrentTransaction { get; private set; }

    public async Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            connection = factory.Create();
        }
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var conn = await GetOpenConnectionAsync(cancellationToken);
        CurrentTransaction = await conn.BeginTransactionAsync(cancellationToken);
        return CurrentTransaction;
    }

    public void ClearTransaction() => CurrentTransaction = null;

    public async ValueTask DisposeAsync()
    {
        if (CurrentTransaction is not null) await CurrentTransaction.DisposeAsync();
        if (connection is not null) await connection.DisposeAsync();
    }
}
```

- [ ] **Step 2:** Append PublicAPI for the two public interfaces (`IDapperConnectionFactory`,
  `IDapperConnectionContext`); `DapperConnectionContext` is internal. Build → 0 warnings.
- [ ] **Step 3: Commit** `git commit -am "feat(dapper): scoped connection + transaction context"`.

### Task 2.6: Tenant query factory (tier-2)

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/Tenancy/ITenantQueryFactory.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper/Tenancy/TenantQueryFactory.cs`

- [ ] **Step 1: Implement** (seeds a SqlKata `Query` via `TenantPredicate`; reads the current tenant from
  `ITenantContext` and bypass from `IDataFilterScope`; `includeGlobalRecords` is a `DapperDataOptions` flag —
  Task 2.10):

```csharp
// ITenantQueryFactory.cs
using SqlKata;
namespace Themia.Framework.Data.Dapper.Tenancy;

/// <summary>Tier-2 entry point: a SqlKata Query for <typeparamref name="T"/> pre-seeded with the tenant
/// predicate + soft-delete filter. Compose joins/filters on it, then execute via Dapper.</summary>
public interface ITenantQueryFactory
{
    Query For<T>();
}
```

```csharp
// TenantQueryFactory.cs
using SqlKata;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Dapper.Mapping;

namespace Themia.Framework.Data.Dapper.Tenancy;

internal sealed class TenantQueryFactory(
    EntityMappingRegistry registry,
    ITenantContext tenantContext,
    IDataFilterScope filterScope,
    DapperDataOptions options) : ITenantQueryFactory
{
    public Query For<T>()
    {
        var map = registry.For<T>();
        var query = new Query(map.Table);
        TenantPredicate.Apply<T>(query, tenantContext.CurrentTenantId, options.IncludeGlobalRecordsForTenants,
            filterScope.IsTenantFilterBypassed, map);
        return query;
    }
}
```

- [ ] **Step 2:** Append PublicAPI for `ITenantQueryFactory`. Build → 0 warnings.
- [ ] **Step 3: Commit** `git commit -am "feat(dapper): tenant-seeded SqlKata query factory (tier-2)"`.

### Task 2.7: Read repository

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/Repositories/DapperReadRepository.cs`

(Behaviourally tested by the conformance suite in Phase 5 — that's where a real DB makes the assertions
meaningful. No unit test here.)

- [ ] **Step 1: Implement** (reads go through tenant-seeded query + translator + compiler + Dapper):

```csharp
using Dapper;
using SqlKata;
using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Sql;
using Themia.Framework.Data.Dapper.Tenancy;
using Themia.Framework.Data.Dapper.Translation;

namespace Themia.Framework.Data.Dapper.Repositories;

internal class DapperReadRepository<T, TKey>(
    IDapperConnectionContext connection,
    ITenantQueryFactory queryFactory,
    EntityMappingRegistry registry,
    ISqlCompiler compiler) : IReadRepository<T, TKey>
    where T : class
{
    protected readonly IDapperConnectionContext Connection = connection;
    protected readonly EntityMappingRegistry Registry = registry;
    protected readonly ISqlCompiler Compiler = compiler;
    protected EntityMapping Map => Registry.For<T>();

    private Query Seeded() => queryFactory.For<T>();

    public async Task<T?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
    {
        var query = Seeded().Where(Map.KeyColumn, id).Limit(1);
        return await QuerySingleAsync(query, cancellationToken);
    }

    public async Task<IReadOnlyList<T>> ListAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var query = Seeded();
        SpecificationTranslator.Apply(query, spec, Map);
        var sql = Compiler.Compile(query);
        var conn = await Connection.GetOpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<T>(new CommandDefinition(sql.Sql, sql.Parameters, Connection.CurrentTransaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<T?> FirstOrDefaultAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var query = Seeded();
        SpecificationTranslator.Apply(query, spec, Map);
        return await QuerySingleAsync(query.Limit(1), cancellationToken);
    }

    public async Task<long> CountAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var query = Seeded();
        if (spec.Criteria is not null) SpecificationTranslator.Apply(StripPaging(query), spec, Map);
        var sql = Compiler.Compile(query.AsCount());
        var conn = await Connection.GetOpenConnectionAsync(cancellationToken);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql.Sql, sql.Parameters, Connection.CurrentTransaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> AnyAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await CountAsync(spec, cancellationToken) > 0;

    public async Task<PagedResult<T>> PageAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(spec, cancellationToken);
        var items = await ListAsync(spec, cancellationToken);
        return new PagedResult<T>(items, total, spec.Skip, spec.Take);
    }

    private async Task<T?> QuerySingleAsync(Query query, CancellationToken cancellationToken)
    {
        var sql = Compiler.Compile(query);
        var conn = await Connection.GetOpenConnectionAsync(cancellationToken);
        return await conn.QueryFirstOrDefaultAsync<T>(new CommandDefinition(sql.Sql, sql.Parameters, Connection.CurrentTransaction, cancellationToken: cancellationToken));
    }

    private static Query StripPaging(Query q) => q;   // count ignores Offset/Limit; AsCount() handles it
}
```

- [ ] **Step 2: Build** `dotnet build src/framework/Themia.Framework.Data.Dapper -c Release` → 0 warnings.
- [ ] **Step 3: Commit** `git commit -am "feat(dapper): DapperReadRepository (spec reads via translator)"`.

### Task 2.8: Write repository + pending operations

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/UnitOfWork/PendingOperation.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper/Repositories/DapperRepository.cs`

- [ ] **Step 1: Implement the pending-op model + repository** (Add/Update/Remove enqueue; the UoW flushes):

```csharp
// PendingOperation.cs
namespace Themia.Framework.Data.Dapper.UnitOfWork;

internal enum PendingKind { Add, Update, Remove }
internal sealed record PendingOperation(PendingKind Kind, object Entity, Type EntityType);

internal interface IPendingOperationSink
{
    void Enqueue(PendingOperation operation);
}
```

```csharp
// DapperRepository.cs
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Sql;
using Themia.Framework.Data.Dapper.Tenancy;
using Themia.Framework.Data.Dapper.UnitOfWork;

namespace Themia.Framework.Data.Dapper.Repositories;

internal sealed class DapperRepository<T, TKey>(
    IDapperConnectionContext connection,
    ITenantQueryFactory queryFactory,
    EntityMappingRegistry registry,
    ISqlCompiler compiler,
    IPendingOperationSink sink)
    : DapperReadRepository<T, TKey>(connection, queryFactory, registry, compiler), IRepository<T, TKey>
    where T : class
{
    public Task AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        sink.Enqueue(new PendingOperation(PendingKind.Add, entity, typeof(T)));
        return Task.CompletedTask;
    }

    public void Update(T entity) => sink.Enqueue(new PendingOperation(PendingKind.Update, entity, typeof(T)));
    public void Remove(T entity) => sink.Enqueue(new PendingOperation(PendingKind.Remove, entity, typeof(T)));
}
```

- [ ] **Step 2: Build → 0 warnings.**
- [ ] **Step 3: Commit** `git commit -am "feat(dapper): DapperRepository (deferred Add/Update/Remove)"`.

### Task 2.9: Unit of Work — flush, stamping, soft-delete, key population

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/UnitOfWork/DapperUnitOfWork.cs`

This is the behavioural heart on the write side. It is proven by the conformance suite (Phase 5); implement
it fully here.

- [ ] **Step 1: Implement** the UoW (pending-op sink + transaction + INSERT/UPDATE/soft-delete via SqlKata):

```csharp
using Dapper;
using SqlKata;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.UnitOfWork;

internal sealed class DapperUnitOfWork(
    IDapperConnectionContext connection,
    EntityMappingRegistry registry,
    ISqlCompiler compiler,
    ITenantContext tenantContext,
    ICurrentUserAccessor currentUser,
    TimeProvider timeProvider) : IUnitOfWork, IPendingOperationSink
{
    private readonly List<PendingOperation> pending = [];

    public void Enqueue(PendingOperation operation) => pending.Add(operation);

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (pending.Count == 0) return 0;

        var ownsTransaction = connection.CurrentTransaction is null;
        if (ownsTransaction) await connection.BeginTransactionAsync(cancellationToken);
        var conn = await connection.GetOpenConnectionAsync(cancellationToken);
        var tx = connection.CurrentTransaction;

        try
        {
            var affected = 0;
            foreach (var op in pending)
                affected += await ExecuteAsync(conn, tx, op, cancellationToken);

            if (ownsTransaction && tx is not null) { await tx.CommitAsync(cancellationToken); connection.ClearTransaction(); }
            pending.Clear();
            return affected;
        }
        catch
        {
            if (ownsTransaction && tx is not null) { await tx.RollbackAsync(cancellationToken); connection.ClearTransaction(); }
            pending.Clear();
            throw;
        }
    }

    public async Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        await connection.BeginTransactionAsync(cancellationToken);
        return new TransactionScope(connection);
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        await using var scope = await BeginTransactionAsync(cancellationToken);
        try { await work(cancellationToken); await SaveChangesAsync(cancellationToken); await scope.CommitAsync(cancellationToken); }
        catch { await scope.RollbackAsync(cancellationToken); throw; }
    }

    private async Task<int> ExecuteAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, PendingOperation op, CancellationToken ct)
    {
        var map = registry.For(op.EntityType);
        var now = timeProvider.GetUtcNow();
        var userId = currentUser.UserId;

        switch (op.Kind)
        {
            case PendingKind.Add:
            {
                StampTenant(op.Entity, map);
                Stamp(op.Entity, nameof(IAuditableEntity.CreatedAt), now);
                if (userId is not null) Stamp(op.Entity, nameof(IAuditableEntity.CreatedBy), userId);

                var values = ColumnValues(op.Entity, map, includeKeyIfAssigned: true, out var keyAssigned);
                var query = new Query(map.Table).AsInsert(values, returnId: !keyAssigned);
                var sql = compiler.Compile(query);
                if (!keyAssigned)
                {
                    var newId = await conn.ExecuteScalarAsync<object>(new CommandDefinition(sql.Sql, sql.Parameters, tx, cancellationToken: ct));
                    map.KeySetter(op.Entity, Convert.ChangeType(newId!, map.KeyType));
                    return 1;
                }
                return await conn.ExecuteAsync(new CommandDefinition(sql.Sql, sql.Parameters, tx, cancellationToken: ct));
            }
            case PendingKind.Update:
            {
                Stamp(op.Entity, nameof(IAuditableEntity.LastModifiedAt), now);
                if (userId is not null) Stamp(op.Entity, nameof(IAuditableEntity.LastModifiedBy), userId);
                var values = ColumnValues(op.Entity, map, includeKeyIfAssigned: false, out _);
                var query = TenantScoped(new Query(map.Table), op.Entity, map).Where(map.KeyColumn, KeyOf(op.Entity, map)).AsUpdate(values);
                var sql = compiler.Compile(query);
                return await conn.ExecuteAsync(new CommandDefinition(sql.Sql, sql.Parameters, tx, cancellationToken: ct));
            }
            case PendingKind.Remove:
            {
                Query query;
                if (op.Entity is ISoftDeletable)
                {
                    var values = new Dictionary<string, object?>
                    {
                        [map.Column(nameof(ISoftDeletable.IsDeleted))] = true,
                        [map.Column(nameof(ISoftDeletable.DeletedAt))] = now,
                        [map.Column(nameof(ISoftDeletable.DeletedBy))] = userId,
                    };
                    query = TenantScoped(new Query(map.Table), op.Entity, map).Where(map.KeyColumn, KeyOf(op.Entity, map)).AsUpdate(values);
                }
                else
                {
                    query = TenantScoped(new Query(map.Table), op.Entity, map).Where(map.KeyColumn, KeyOf(op.Entity, map)).AsDelete();
                }
                var sql = compiler.Compile(query);
                return await conn.ExecuteAsync(new CommandDefinition(sql.Sql, sql.Parameters, tx, cancellationToken: ct));
            }
            default: return 0;
        }
    }

    private void StampTenant(object entity, EntityMapping map)
    {
        if (entity is ITenantEntity te && te.TenantId is null)
            te.TenantId = tenantContext.CurrentTenantId;
    }

    private Query TenantScoped(Query q, object entity, EntityMapping map)
    {
        if (entity is ITenantEntity && tenantContext.CurrentTenantId is { } t)
            q.Where(map.Column(nameof(ITenantEntity.TenantId)), t.Value);
        return q;
    }

    private static object KeyOf(object entity, EntityMapping map) =>
        entity.GetType().GetProperty(map.KeyProperty)!.GetValue(entity)!;

    private static void Stamp(object entity, string property, object? value) =>
        entity.GetType().GetProperty(property)?.SetValue(entity, value);

    private static Dictionary<string, object?> ColumnValues(object entity, EntityMapping map, bool includeKeyIfAssigned, out bool keyAssigned)
    {
        var values = new Dictionary<string, object?>();
        keyAssigned = false;
        foreach (var (prop, column) in map.Columns)
        {
            var pi = entity.GetType().GetProperty(prop);
            if (pi is null) continue;
            var value = pi.GetValue(entity);
            if (prop == map.KeyProperty)
            {
                keyAssigned = value is not null && !IsDefault(value);
                if (!includeKeyIfAssigned || !keyAssigned) continue;   // let the DB generate when unassigned
            }
            if (value is TenantId tid) value = tid.Value;
            else if (value is TenantId?) value = ((TenantId?)value)?.Value;
            values[column] = value;
        }
        return values;
    }

    private static bool IsDefault(object value) =>
        value is int i ? i == 0 : value is long l ? l == 0 : value is Guid g && g == Guid.Empty;

    private sealed class TransactionScope(IDapperConnectionContext connection) : ITransactionScope
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        { if (connection.CurrentTransaction is { } tx) { await tx.CommitAsync(cancellationToken); connection.ClearTransaction(); } }
        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        { if (connection.CurrentTransaction is { } tx) { await tx.RollbackAsync(cancellationToken); connection.ClearTransaction(); } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

> Note the `TenantId` → `string` unwrapping in `ColumnValues` and the soft-delete branch storing the 3
> delete columns (leaving `restored_at`/`restored_by` null). `EntityMappingRegistry` needs a `For(Type)`
> overload (add it: `public EntityMapping For(Type t) => cache.GetOrAdd(...)` mirroring `For<T>()`).

- [ ] **Step 2:** Add the `EntityMappingRegistry.For(Type)` overload used above. Build → 0 warnings.
- [ ] **Step 3: Commit** `git commit -am "feat(dapper): DapperUnitOfWork — flush, audit/tenant stamping, soft-delete, key population"`.

### Task 2.10: Options + DI (`AddThemiaDapperCore`)

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper/DapperDataOptions.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper/Auditing/NullCurrentUserAccessor.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper/DependencyInjection/DapperDataServiceCollectionExtensions.cs`

- [ ] **Step 1: Implement options + null user accessor + DI:**

```csharp
// DapperDataOptions.cs
namespace Themia.Framework.Data.Dapper;
public sealed class DapperDataOptions
{
    /// <summary>When true, tenant queries also match rows with a NULL tenant_id (global/shared records).</summary>
    public bool IncludeGlobalRecordsForTenants { get; set; }
    public Action<EntityMappingRegistryConfigurator>? ConfigureMappings { get; set; }
}
public sealed class EntityMappingRegistryConfigurator(Mapping.EntityMappingRegistry registry)
{
    public EntityMappingRegistryConfigurator Map<T>(Mapping.EntityMapping mapping) { registry.Register<T>(mapping); return this; }
}
```

```csharp
// Auditing/NullCurrentUserAccessor.cs
using Themia.Framework.Data.Abstractions.Auditing;
namespace Themia.Framework.Data.Dapper.Auditing;
/// <summary>Default audit-user source: no user. Replace via DI to stamp CreatedBy/etc.</summary>
public sealed class NullCurrentUserAccessor : ICurrentUserAccessor { public string? UserId => null; }
```

```csharp
// DependencyInjection/DapperDataServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Abstractions.Filtering;   // IDataFilterScope + DataFilterScope both here
using Themia.Framework.Data.Dapper.Auditing;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Repositories;
using Themia.Framework.Data.Dapper.Tenancy;
using Themia.Framework.Data.Dapper.UnitOfWork;

namespace Themia.Framework.Data.Dapper.DependencyInjection;

public static class DapperDataServiceCollectionExtensions
{
    /// <summary>Registers the engine-agnostic Dapper data services. An engine package (e.g. PostgreSql)
    /// must also register an <see cref="IDapperConnectionFactory"/> and <see cref="Sql.ISqlCompiler"/>.</summary>
    public static IServiceCollection AddThemiaDapperCore(this IServiceCollection services, Action<DapperDataOptions>? configure = null)
    {
        DapperConfiguration.EnsureConfigured();

        var options = new DapperDataOptions();
        configure?.Invoke(options);
        var registry = new EntityMappingRegistry();
        options.ConfigureMappings?.Invoke(new EntityMappingRegistryConfigurator(registry));

        services.AddSingleton(options);
        services.AddSingleton(registry);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICurrentUserAccessor, NullCurrentUserAccessor>();
        services.TryAddScoped<IDataFilterScope, DataFilterScope>();
        services.AddScoped<DapperConnectionContext>();
        services.AddScoped<IDapperConnectionContext>(sp => sp.GetRequiredService<DapperConnectionContext>());
        services.AddScoped<ITenantQueryFactory, TenantQueryFactory>();
        services.AddScoped<DapperUnitOfWork>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<DapperUnitOfWork>());
        services.AddScoped<IPendingOperationSink>(sp => sp.GetRequiredService<DapperUnitOfWork>());
        services.AddScoped(typeof(IReadRepository<,>), typeof(DapperReadRepository<,>));
        services.AddScoped(typeof(IRepository<,>), typeof(DapperRepository<,>));
        return services;
    }
}
```

- [ ] **Step 2:** Append PublicAPI for the public types (`DapperDataOptions`, `EntityMappingRegistryConfigurator`,
  `NullCurrentUserAccessor`, `DapperDataServiceCollectionExtensions`). Build → 0 warnings.
- [ ] **Step 3: Commit** `git commit -am "feat(dapper): options + null user accessor + AddThemiaDapperCore DI"`.

---

## PHASE 3 — PostgreSQL engine (`Themia.Framework.Data.Dapper.PostgreSql`)

### Task 3.1: Postgres compiler + connection factory + DI

**Files:**
- Create: `src/framework/Themia.Framework.Data.Dapper.PostgreSql/PostgresSqlCompiler.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.PostgreSql/NpgsqlConnectionFactory.cs`
- Create: `src/framework/Themia.Framework.Data.Dapper.PostgreSql/DependencyInjection/PostgresDapperServiceCollectionExtensions.cs`

- [ ] **Step 1: Implement** the compiler, factory (tenant-resolved connection string, mirroring
  `PostgresDatabaseProvider.ResolveConnectionString`), and DI:

```csharp
// PostgresSqlCompiler.cs
using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Data.Dapper.Sql;
namespace Themia.Framework.Data.Dapper.PostgreSql;

internal sealed class PostgresSqlCompiler : ISqlCompiler
{
    private readonly PostgresCompiler compiler = new();
    public CompiledSql Compile(Query query)
    {
        var r = compiler.Compile(query);
        return new CompiledSql(r.Sql, r.NamedBindings);
    }
}
```

```csharp
// NpgsqlConnectionFactory.cs
using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Themia.Framework.Data.Dapper.Connection;
using Themia.MultiTenancy.Abstractions;   // ITenantAccessor
namespace Themia.Framework.Data.Dapper.PostgreSql;

internal sealed class NpgsqlConnectionFactory(IConfiguration configuration, IServiceProvider serviceProvider) : IDapperConnectionFactory
{
    private const string DefaultConnectionName = "Default";
    public DbConnection Create() => new NpgsqlConnection(Resolve());

    private string Resolve()
    {
        var tenantCs = (serviceProvider.GetService(typeof(ITenantAccessor)) as ITenantAccessor)?.Current?.ConnectionString;
        if (!string.IsNullOrWhiteSpace(tenantCs)) return tenantCs;
        var cs = configuration.GetConnectionString(DefaultConnectionName);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException($"No tenant connection string resolved and connection string '{DefaultConnectionName}' was not found.");
        return cs;
    }
}
```

```csharp
// DependencyInjection/PostgresDapperServiceCollectionExtensions.cs
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.DependencyInjection;
using Themia.Framework.Data.Dapper.Sql;
namespace Themia.Framework.Data.Dapper.PostgreSql.DependencyInjection;

public static class PostgresDapperServiceCollectionExtensions
{
    /// <summary>Registers the Themia Dapper data layer on PostgreSQL. Resolves the connection string per
    /// scope from ITenantAccessor.Current?.ConnectionString, falling back to the "Default" connection string.</summary>
    public static IServiceCollection AddThemiaDapperPostgres(this IServiceCollection services, IConfiguration configuration, Action<DapperDataOptions>? configure = null)
    {
        services.AddThemiaDapperCore(configure);
        services.AddSingleton(configuration);
        services.AddScoped<IDapperConnectionFactory>(sp => new NpgsqlConnectionFactory(configuration, sp));
        services.AddSingleton<ISqlCompiler, PostgresSqlCompiler>();
        return services;
    }
}
```

- [ ] **Step 2:** Append PublicAPI for `PostgresDapperServiceCollectionExtensions`. Build the PostgreSql
  project → 0 warnings.
- [ ] **Step 3: Commit** `git commit -am "feat(dapper): PostgreSQL engine — compiler, connection factory, AddThemiaDapperPostgres"`.

---

## PHASE 4 — EF retrofit (`Themia.Framework.Data.EFCore`)

### Task 4.1: EF repositories + UoW + DI

**Files:**
- Modify: `src/framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj` (add ProjectReference to Abstractions)
- Create: `src/framework/Themia.Framework.Data.EFCore/Repositories/EfReadRepository.cs`
- Create: `src/framework/Themia.Framework.Data.EFCore/Repositories/EfRepository.cs`
- Create: `src/framework/Themia.Framework.Data.EFCore/UnitOfWork/EfUnitOfWork.cs`
- Create: `src/framework/Themia.Framework.Data.EFCore/Extensions/RepositoryServiceCollectionExtensions.cs`
- Modify: EFCore `PublicAPI.Unshipped.txt`

- [ ] **Step 1: Add the ProjectReference** to Abstractions in the EFCore csproj's `<ItemGroup>` of project
  references:

```xml
<ProjectReference Include="../Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
```

- [ ] **Step 2: Implement the adapters.** `EfReadRepository` applies `spec.Criteria` + ordering + paging to
  the `DbSet`, honouring the bypass scope via `IgnoreQueryFilters()`:

```csharp
// Repositories/EfReadRepository.cs
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
namespace Themia.Framework.Data.EFCore.Repositories;

public class EfReadRepository<T, TKey>(ThemiaDbContext context, IDataFilterScope filterScope) : IReadRepository<T, TKey>
    where T : class
{
    protected readonly ThemiaDbContext Context = context;

    protected IQueryable<T> Query(ISpecification<T> spec)
    {
        IQueryable<T> q = Context.Set<T>();
        if (spec.IgnoreTenantFilter || filterScope.IsTenantFilterBypassed) q = q.IgnoreQueryFilters();
        if (spec.Criteria is not null) q = q.Where(spec.Criteria);

        IOrderedQueryable<T>? ordered = null;
        foreach (var o in spec.OrderBy)
            ordered = ordered is null
                ? (o.Descending ? q.OrderByDescending(o.KeySelector) : q.OrderBy(o.KeySelector))
                : (o.Descending ? ordered.ThenByDescending(o.KeySelector) : ordered.ThenBy(o.KeySelector));
        if (ordered is not null) q = ordered;

        if (spec.Skip is { } s) q = q.Skip(s);
        if (spec.Take is { } t) q = q.Take(t);
        return q;
    }

    public async Task<T?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
        => await Context.Set<T>().FindAsync([id!], cancellationToken);

    public async Task<IReadOnlyList<T>> ListAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await Query(spec).ToListAsync(cancellationToken);

    public async Task<T?> FirstOrDefaultAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await Query(spec).FirstOrDefaultAsync(cancellationToken);

    public async Task<long> CountAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await CountQuery(spec).LongCountAsync(cancellationToken);

    public async Task<bool> AnyAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await CountQuery(spec).AnyAsync(cancellationToken);

    public async Task<PagedResult<T>> PageAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(spec, cancellationToken);
        var items = await ListAsync(spec, cancellationToken);
        return new PagedResult<T>(items, total, spec.Skip, spec.Take);
    }

    private IQueryable<T> CountQuery(ISpecification<T> spec)
    {
        IQueryable<T> q = Context.Set<T>();
        if (spec.IgnoreTenantFilter || filterScope.IsTenantFilterBypassed) q = q.IgnoreQueryFilters();
        if (spec.Criteria is not null) q = q.Where(spec.Criteria);
        return q;
    }
}
```

> Simplify the ordering loop in review (the ternary above is a placeholder for the
> first-vs-subsequent `OrderBy`/`ThenBy` distinction). Correct form: track whether any ordering has been
> applied and use `ThenBy`/`ThenByDescending` after the first. Implement it cleanly:
>
> ```csharp
> IOrderedQueryable<T>? ordered = null;
> foreach (var o in spec.OrderBy)
>     ordered = ordered is null
>         ? (o.Descending ? q.OrderByDescending(o.KeySelector) : q.OrderBy(o.KeySelector))
>         : (o.Descending ? ordered.ThenByDescending(o.KeySelector) : ordered.ThenBy(o.KeySelector));
> if (ordered is not null) q = ordered;
> ```

```csharp
// Repositories/EfRepository.cs
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
namespace Themia.Framework.Data.EFCore.Repositories;

public sealed class EfRepository<T, TKey>(ThemiaDbContext context, IDataFilterScope filterScope)
    : EfReadRepository<T, TKey>(context, filterScope), IRepository<T, TKey> where T : class
{
    public async Task AddAsync(T entity, CancellationToken cancellationToken = default)
        => await Context.Set<T>().AddAsync(entity, cancellationToken);
    public void Update(T entity) => Context.Set<T>().Update(entity);
    public void Remove(T entity) => Context.Set<T>().Remove(entity);   // ThemiaDbContext converts to soft-delete on SaveChanges
}
```

```csharp
// UnitOfWork/EfUnitOfWork.cs
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.Abstractions.UnitOfWork;
namespace Themia.Framework.Data.EFCore.UnitOfWork;

public sealed class EfUnitOfWork(ThemiaDbContext context) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => context.SaveChangesAsync(cancellationToken);

    public async Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => new EfTransactionScope(await context.Database.BeginTransactionAsync(cancellationToken));

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await context.Database.BeginTransactionAsync(cancellationToken);
            await work(cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
    }

    private sealed class EfTransactionScope(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx) : ITransactionScope
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => tx.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken = default) => tx.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => tx.DisposeAsync();
    }
}
```

```csharp
// Extensions/RepositoryServiceCollectionExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Abstractions.Filtering;   // shared DataFilterScope (lives in Abstractions)
using Themia.Framework.Data.EFCore.Repositories;
using Themia.Framework.Data.EFCore.UnitOfWork;
namespace Themia.Framework.Data.EFCore.Extensions;

public static class RepositoryServiceCollectionExtensions
{
    /// <summary>Registers the shared repository/UoW contracts backed by the EF Core ThemiaDbContext.</summary>
    public static IServiceCollection AddThemiaDataRepositories<TContext>(this IServiceCollection services)
        where TContext : ThemiaDbContext
    {
        services.TryAddScoped<IDataFilterScope, DataFilterScope>();
        services.AddScoped<ThemiaDbContext>(sp => sp.GetRequiredService<TContext>());
        services.AddScoped(typeof(IReadRepository<,>), typeof(EfReadRepository<,>));
        services.AddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        return services;
    }
}
```

`DataFilterScope` lives in `Themia.Framework.Data.Abstractions` (Task 2.4), so both DI extensions reference
the same shared `IDataFilterScope`/`DataFilterScope` — no EFCore→Dapper dependency.

- [ ] **Step 3:** Build the solution → 0 warnings (the EF adapter has no behaviour change for existing
  direct-`DbContext` users — it's additive registration only).
- [ ] **Step 4: Commit** `git commit -am "feat(data): EF Core adapters for the shared repository/UoW contracts"`.

---

## PHASE 5 — Conformance suite

### Task 5.1: Conformance base + test entity

**Files:**
- Create: `tests/Themia.Framework.Data.Dapper.Conformance/TestEntities.cs`
- Create: `tests/Themia.Framework.Data.Dapper.Conformance/DataLayerConformanceTests.cs`

The base is an abstract xUnit class with `[Fact]`s written **only** against the shared abstraction
(`IRepository<,>`, `IUnitOfWork`, `IDataFilterScope`). Subclasses (Task 5.2) supply a configured
`IServiceProvider` + reset hook, so the **same tests run against Dapper-PG and EF-PG**.

- [ ] **Step 1: Define the shared test entity** (a `SoftDeletableEntity<Guid>` + `ITenantEntity` — exercises
  tenant + audit + soft-delete; Guid key avoids the store-generated-key path so the same entity works on
  both layers with client-assigned keys):

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
namespace Themia.Framework.Data.Dapper.Conformance;

public class Widget : SoftDeletableEntity<Guid>, ITenantEntity
{
    public TenantId? TenantId { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public void SetId(Guid id) => Id = id;   // Entity<TId>.Id has protected set; expose for client-assigned keys in tests
}

public sealed class WidgetByNameSpec : Specification<Widget>
{
    public WidgetByNameSpec(string name) => Where(w => w.Name == name);
}

// Returned by NewScopeAsync; disposes the underlying DI scope.
public sealed record ConformanceScope(
    IAsyncDisposable Scope,
    Themia.Framework.Data.Abstractions.Repositories.IRepository<Widget, Guid> Repo,
    Themia.Framework.Data.Abstractions.UnitOfWork.IUnitOfWork Uow,
    Themia.Framework.Data.Abstractions.Filtering.IDataFilterScope Filter) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Scope.DisposeAsync();
}
```

- [ ] **Step 2: Write the conformance facts** (abstract members for the provider hooks):

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Xunit;
namespace Themia.Framework.Data.Dapper.Conformance;

public abstract class DataLayerConformanceTests
{
    // A DI scope wired for the given tenant; provider supplies repo/uow/filter via the ConformanceScope record.
    protected abstract Task<ConformanceScope> NewScopeAsync(TenantId? tenant);
    protected abstract Task ResetAsync();

    private static Widget NewWidget(string name, int qty) { var w = new Widget { Name = name, Quantity = qty }; w.SetId(Guid.NewGuid()); return w; }

    // PATTERN (apply to every fact): acquire the scope via the record, then use s.Repo / s.Uow / s.Filter.
    [Fact]
    public async Task Add_Then_GetById_RoundTrips_AndStampsAudit()
    {
        await ResetAsync();
        await using var s = await NewScopeAsync(new TenantId("acme"));
        var w = NewWidget("drill", 5);
        await s.Repo.AddAsync(w);
        await s.Uow.SaveChangesAsync();

        var fetched = await s.Repo.GetByIdAsync(w.Id);
        Assert.NotNull(fetched);
        Assert.Equal("drill", fetched!.Name);
        Assert.True(fetched.CreatedAt > DateTimeOffset.MinValue);   // audit stamped
        Assert.Equal(new TenantId("acme"), fetched.TenantId);       // tenant stamped
    }

    [Fact]
    public async Task Tenant_A_Cannot_See_Tenant_B_Rows()
    {
        await ResetAsync();
        await using (var _ = await NewScopeAsync(new TenantId("a"), out var repoA, out var uowA, out _))
        { await repoA.AddAsync(NewWidget("a-only", 1)); await uowA.SaveChangesAsync(); }

        await using var __ = await NewScopeAsync(new TenantId("b"), out var repoB, out _, out _);
        var visible = await repoB.ListAsync(new WidgetByNameSpec("a-only"));
        Assert.Empty(visible);
    }

    [Fact]
    public async Task Remove_SoftDeletes_And_HidesFromQueries()
    {
        await ResetAsync();
        await using var _ = await NewScopeAsync(new TenantId("acme"), out var repo, out var uow, out _);
        var w = NewWidget("temp", 1);
        await repo.AddAsync(w); await uow.SaveChangesAsync();

        repo.Remove(w); await uow.SaveChangesAsync();

        Assert.Null(await repo.GetByIdAsync(w.Id));   // hidden by soft-delete filter
    }

    [Fact]
    public async Task BypassTenantFilter_RevealsOtherTenants()
    {
        await ResetAsync();
        await using (var _ = await NewScopeAsync(new TenantId("a"), out var repoA, out var uowA, out _))
        { await repoA.AddAsync(NewWidget("shared", 1)); await uowA.SaveChangesAsync(); }

        await using var __ = await NewScopeAsync(new TenantId("b"), out var repoB, out _, out var filter);
        using (filter.BypassTenantFilter())
        {
            var all = await repoB.ListAsync(new WidgetByNameSpec("shared"));
            Assert.Single(all);
        }
    }

    [Fact]
    public async Task Page_ReturnsItemsAndTotal()
    {
        await ResetAsync();
        await using var _ = await NewScopeAsync(new TenantId("acme"), out var repo, out var uow, out _);
        for (var i = 0; i < 5; i++) await repo.AddAsync(NewWidget($"w{i}", i));
        await uow.SaveChangesAsync();

        var page = await repo.PageAsync(new PagedSpec());
        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Items.Count);
    }

    private sealed class PagedSpec : Specification<Widget> { public PagedSpec() { OrderBy(w => w.Name); Page(0, 2); } }

    [Fact]
    public async Task Transaction_Rollback_DiscardsWrites()
    {
        await ResetAsync();
        await using var _ = await NewScopeAsync(new TenantId("acme"), out var repo, out var uow, out _);
        await using (var tx = await uow.BeginTransactionAsync())
        {
            await repo.AddAsync(NewWidget("ghost", 1));
            await uow.SaveChangesAsync();
            await tx.RollbackAsync();
        }
        Assert.Empty(await repo.ListAsync(new WidgetByNameSpec("ghost")));
    }
}
```

> Note: the `out` params in an `async` signature don't compile — in the real implementation return a small
> `record ConformanceScope(IAsyncDisposable Scope, IRepository<Widget,Guid> Repo, IUnitOfWork Uow, IDataFilterScope Filter)`
> from `NewScopeAsync` and destructure it. Adjust the facts accordingly (mechanical). Keep the assertions.

- [ ] **Step 3: Build** the Conformance project → 0 warnings (it won't run; it's a library).
- [ ] **Step 4: Commit** `git commit -am "test(data): provider-agnostic conformance base + Widget test entity"`.

### Task 5.2: Postgres integration — run the base against Dapper AND EF

**Files:**
- Create: `tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/PostgresContainerFixture.cs`
- Create: `tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/DapperPostgresConformanceTests.cs`
- Create: `tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/EfPostgresConformanceTests.cs`
- Create: `tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/WidgetDbContext.cs` (EF side)

- [ ] **Step 1: Container fixture** (Testcontainers PG; creates the `widgets` table with snake_case columns):

```csharp
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
namespace Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("themia_dapper_tests").WithUsername("postgres").WithPassword("postgres").WithCleanUp(true).Build();

    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE widgets (
                id uuid PRIMARY KEY,
                tenant_id varchar(100) NULL,
                name varchar(200) NOT NULL,
                quantity integer NOT NULL,
                created_at timestamptz NOT NULL,
                created_by varchar(100) NULL,
                last_modified_at timestamptz NULL,
                last_modified_by varchar(100) NULL,
                is_deleted boolean NOT NULL DEFAULT false,
                deleted_at timestamptz NULL,
                deleted_by varchar(100) NULL,
                restored_at timestamptz NULL,
                restored_by varchar(100) NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE widgets";
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();
}
```

- [ ] **Step 2: Dapper conformance subclass** — wires `AddThemiaDapperPostgres` + a per-tenant scope. Build
  the `IServiceProvider` with a `ConnectionStrings:Default` pointed at the container, and a per-scope
  `ITenantContext`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.Dapper.PostgreSql.DependencyInjection;
using Xunit;
namespace Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests;

public sealed class DapperPostgresConformanceTests(PostgresContainerFixture fixture)
    : DataLayerConformanceTests, IClassFixture<PostgresContainerFixture>
{
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override Task<ConformanceScope> NewScopeAsync(TenantId? tenant)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString }).Build());
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        services.AddThemiaDapperPostgres(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString }).Build());
        var sp = services.BuildServiceProvider();
        var scope = sp.CreateAsyncScope();
        var s = scope.ServiceProvider;
        return Task.FromResult(new ConformanceScope(scope,
            s.GetRequiredService<IRepository<Widget, Guid>>(),
            s.GetRequiredService<IUnitOfWork>(),
            s.GetRequiredService<IDataFilterScope>()));
    }
}
```

> `ConformanceScope` (defined in the Conformance project per Task 5.1's note) = `record(IAsyncDisposable Scope,
> IRepository<Widget,Guid> Repo, IUnitOfWork Uow, IDataFilterScope Filter) : IAsyncDisposable` delegating
> dispose to `Scope`.

- [ ] **Step 3: EF conformance subclass** — a `WidgetDbContext : ThemiaDbContext` with a `DbSet<Widget>`
  (snake_case via the provider), wired with `AddThemiaPostgres<WidgetDbContext>` + `AddThemiaDataRepositories<WidgetDbContext>`,
  and a per-scope `ITenantContext`. Same `NewScopeAsync` shape, resolving the EF-backed `IRepository/IUnitOfWork`.
  (Reuse `EnsureCreated` to build the schema, or the shared `widgets` table — point the EF context at the
  same table names.)

- [ ] **Step 4: Run the integration suite** (Docker required):

Run: `dotnet test tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests -c Release`
Expected: all conformance facts pass for **both** `DapperPostgresConformanceTests` and
`EfPostgresConformanceTests` — proving both layers honour the contract identically.

- [ ] **Step 5: Commit** `git commit -am "test(data): PG Testcontainers conformance — Dapper and EF honour the shared contract"`.

---

## PHASE 6 — Release prep

### Task 6.1: CHANGELOG + solution green

**Files:**
- Modify: `CHANGELOG.md` (under `## [Unreleased]`)

- [ ] **Step 1: Add the CHANGELOG entry** under `## [Unreleased]`:

```markdown
### Added

- **`Themia.Framework.Data.Abstractions`** — provider-agnostic data contracts (`ISpecification<T>`,
  `IReadRepository`/`IRepository`, `IUnitOfWork`, `IDataFilterScope`, `ICurrentUserAccessor`).
- **`Themia.Framework.Data.Dapper`** + **`Themia.Framework.Data.Dapper.PostgreSql`** — a Dapper + SqlKata
  data layer implementing the shared contracts with tenant isolation, audit, soft-delete, and unit-of-work,
  plus a tenant-seeded native-SqlKata path (`ITenantQueryFactory`). PostgreSQL this release.
- **`Themia.Framework.Data.EFCore`** now also implements the shared contracts (`AddThemiaDataRepositories<TContext>`),
  so app code written against the abstraction runs on either layer.
```

- [ ] **Step 2: Full clean build + all unit tests.**

Run: `dotnet build Themia.sln -c Release --no-incremental` → `0 Warning(s)`.
Run: `dotnet test Themia.sln -c Release --filter "FullyQualifiedName!~IntegrationTests"` → all green.

- [ ] **Step 3: Run the PG integration suite once more** (Docker) → green.
- [ ] **Step 4: Commit** `git commit -am "docs(data): CHANGELOG for the Dapper data layer (0.4.1)"`.

### Task 6.2: PublicAPI shipped promotion is NOT done here

Per the repo's release process, `PublicAPI.Unshipped.txt` → `Shipped.txt` promotion and the `<Version>` bump
happen in the **release PR**, not the feature PR. Leave the unshipped surfaces as-is; the release PR for 0.4.1
will bump `<Version>` to `0.4.1`, promote PublicAPI, and publish.

---

## Final review (after all tasks)

Dispatch a final code review over the whole branch (the subagent-driven skill does this automatically). Focus
areas: the translator's expression coverage + fail-fast, the UoW transaction/rollback + key population +
`TenantId`→string unwrapping, the `DataFilterScope` location (Abstractions, not Dapper), Npgsql nullable-string
`DbType` needs in writes (add explicit `DbType.String` if integration surfaces a null-inference error), and
that the EF adapter introduces no behaviour change for existing direct-`DbContext` users.
