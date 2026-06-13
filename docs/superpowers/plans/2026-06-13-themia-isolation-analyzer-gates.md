# Themia 0.4.9 — Tenant-isolation analyzer gates Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship two Roslyn analyzers — THEMIA103 (raw Dapper connection) and THEMIA104 (`DbSet.Find`) — that flag the two remaining tenant-isolation bypasses outside the data-access layer, as Warnings, and flow them to adopters of the Themia data packages.

**Architecture:** Two `DiagnosticAnalyzer` classes added to the existing `Themia.Analyzers` (`netstandard2.0`). Each uses `CompilationStart` to resolve its target type once (skipping projects that don't reference Dapper/EF and the `Themia.Framework.Data.*` assemblies themselves), then an `OperationAction` over invocations. Descriptors are built locally with category `Themia.Isolation`. The analyzer is dogfooded on Themia's own `src/` production projects via `Directory.Build.props`, and flowed to adopters by making it a normal NuGet dependency of the three data packages.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis.CSharp` 4.14.0), `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit` 1.1.2, FluentMigrator-era central package management.

**Spec:** `docs/superpowers/specs/2026-06-13-themia-isolation-analyzer-gates-design.md`

**Branch:** `feat/themia-isolation-analyzers` (already created).

---

## File structure

| File | Responsibility |
|---|---|
| `src/tooling/Themia.Analyzers/Diagnostics/DiagnosticDescriptors.cs` | **Modify** — add THEMIA103/104 descriptors (category `Themia.Isolation`). |
| `src/tooling/Themia.Analyzers/DataLayerScope.cs` | **Create** — shared `IsDataLayerAssembly` check. |
| `src/tooling/Themia.Analyzers/DbSetFindBypassAnalyzer.cs` | **Create** — THEMIA104. |
| `src/tooling/Themia.Analyzers/RawConnectionBypassAnalyzer.cs` | **Create** — THEMIA103. |
| `src/tooling/Themia.Analyzers/AnalyzerReleases.Unshipped.md` | **Modify** — add the two rules. |
| `src/tooling/Themia.Analyzers/PublicAPI.Unshipped.txt` | **Modify** — new public analyzer types + descriptor fields. |
| `src/tooling/Themia.Analyzers/Themia.Analyzers.csproj` | **Modify** — drop `DevelopmentDependency`. |
| `tests/Themia.Analyzers.Tests/DbSetFindBypassAnalyzerTests.cs` | **Create** — THEMIA104 tests. |
| `tests/Themia.Analyzers.Tests/RawConnectionBypassAnalyzerTests.cs` | **Create** — THEMIA103 tests. |
| `docs/analyzers/THEMIA103.md`, `docs/analyzers/THEMIA104.md` | **Create** — per-rule help docs. |
| `src/Directory.Build.props`, `src/tooling/Directory.Build.props` | **Create** — dogfood the analyzer on `src/` production projects (tooling shadows it out). |
| `src/framework/Themia.Framework.Data.Abstractions/…csproj`, `…Dapper/…csproj`, `…EFCore/…csproj` | **Modify** — flow the analyzer to adopters. |
| `Directory.Build.props` `<Version>`, `CHANGELOG.md`, `MIGRATION.md` | **Modify** — release. |

---

### Task 1: Diagnostic descriptors + data-layer scope helper

**Files:**
- Modify: `src/tooling/Themia.Analyzers/Diagnostics/DiagnosticDescriptors.cs`
- Create: `src/tooling/Themia.Analyzers/DataLayerScope.cs`

- [ ] **Step 1: Add the two isolation descriptors**

Replace the body of `src/tooling/Themia.Analyzers/Diagnostics/DiagnosticDescriptors.cs` with (keeps the existing 101/102, adds 103/104 via a local factory so the category is `Themia.Isolation` rather than the shared helper's hardcoded `Themia.DI`):

```csharp
using Themia.Generators.Abstractions.Diagnostics;
using Microsoft.CodeAnalysis;

namespace Themia.Analyzers.Diagnostics;

internal static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor SwallowLogRethrow =
        ThemiaDiagnostics.CreateWarning(
            "THEMIA101",
            "Catch logs and rethrows",
            "Catch block logs and rethrows; let the top-level handler log it instead.");

    public static readonly DiagnosticDescriptor SyncOverAsync =
        ThemiaDiagnostics.CreateWarning(
            "THEMIA102",
            "Synchronous work wrapped in Task.FromResult",
            "'{0}' wraps synchronous work in Task.FromResult; provide a genuinely async implementation.");

    // The tenant-isolation gates get their own category so adopters can configure them as a group via
    // .editorconfig (dotnet_analyzer_diagnostic.category-Themia.Isolation.severity). Built locally rather
    // than via ThemiaDiagnostics.CreateWarning, which hardcodes the Themia.DI category.
    private const string IsolationCategory = "Themia.Isolation";

    private static DiagnosticDescriptor IsolationWarning(string id, string title, string message) =>
        new(
            id,
            title,
            message,
            IsolationCategory,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: null,
            helpLinkUri: $"https://github.com/klomkling/themia/blob/main/docs/analyzers/{id}.md");

    public static readonly DiagnosticDescriptor RawConnectionBypass = IsolationWarning(
        "THEMIA103",
        "Raw Dapper connection bypasses tenant isolation",
        "Raw Dapper connection bypasses tenant isolation; use ITenantQueryFactory.For<T>() for ad-hoc " +
        "tenant-scoped queries, or suppress THEMIA103 with a justification for a deliberate bypass.");

    public static readonly DiagnosticDescriptor DbSetFindBypass = IsolationWarning(
        "THEMIA104",
        "DbSet.Find bypasses the tenant post-check",
        "DbSet.Find/FindAsync bypasses Themia's tenant post-check for already-tracked entities; use " +
        "DbContext.FindAsync<T>() or IReadRepository.GetByIdAsync(). Suppress THEMIA104 with a " +
        "justification for a deliberate bypass.");
}
```

- [ ] **Step 2: Create the scope helper**

Create `src/tooling/Themia.Analyzers/DataLayerScope.cs`:

```csharp
using System;

namespace Themia.Analyzers;

/// <summary>
/// The Themia.Framework.Data.* assemblies legitimately own the raw primitives (repositories, the Dapper
/// connection context, the guarded ThemiaDbContext.Find overrides that call base.Find). The isolation
/// analyzers stay silent there and fire everywhere else — adopter code and Themia.Modules.* alike.
/// </summary>
internal static class DataLayerScope
{
    public static bool IsDataLayerAssembly(string? assemblyName) =>
        assemblyName is not null &&
        assemblyName.StartsWith("Themia.Framework.Data", StringComparison.Ordinal);
}
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/tooling/Themia.Analyzers/Themia.Analyzers.csproj`
Expected: Build succeeded, 0 errors. (RS2008 "enable analyzer release tracking" is suppressed in this csproj's `NoWarn`; the new IDs are added to release tracking in Task 4.)

If an RS10xx descriptor-authoring rule fires on the direct `DiagnosticDescriptor` construction (e.g. RS1007/RS1015 localizability, RS1033), add that exact ID to the `<NoWarn>` in `src/tooling/Themia.Analyzers/Themia.Analyzers.csproj` alongside the existing `RS1032;RS2008;NU5128` — these are analyzer-authoring style rules, not correctness. (The shared `ThemiaDiagnostics.CreateWarning` constructs descriptors the same way, so this is consistent with the codebase.)

- [ ] **Step 4: Commit**

```bash
git add src/tooling/Themia.Analyzers/Diagnostics/DiagnosticDescriptors.cs src/tooling/Themia.Analyzers/DataLayerScope.cs
git commit -m "feat(analyzers): add THEMIA103/104 isolation descriptors + data-layer scope"
```

---

### Task 2: THEMIA104 — DbSet.Find analyzer (TDD)

**Files:**
- Create: `tests/Themia.Analyzers.Tests/DbSetFindBypassAnalyzerTests.cs`
- Create: `src/tooling/Themia.Analyzers/DbSetFindBypassAnalyzer.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Themia.Analyzers.Tests/DbSetFindBypassAnalyzerTests.cs`. Tests declare inline stub types so `GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1")` resolves without referencing EF. The reported location is the whole invocation expression, so `{|#0:…|}` wraps the full call.

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Themia.Analyzers.Tests;

public class DbSetFindBypassAnalyzerTests
{
    // Minimal stubs so the analyzer can bind Microsoft.EntityFrameworkCore.DbSet<T> and a guarded
    // DbContext.FindAsync<T> without referencing the EF packages.
    private const string EfStubs = @"
namespace Microsoft.EntityFrameworkCore {
    public class DbSet<T> {
        public T Find(params object[] keyValues) => default!;
        public System.Threading.Tasks.ValueTask<T> FindAsync(params object[] keyValues) => default;
    }
    public class DbContext {
        public DbSet<T> Set<T>() => new DbSet<T>();
        public System.Threading.Tasks.ValueTask<T> FindAsync<T>(params object[] keyValues) => default;
    }
}";

    [Fact]
    public async Task DbSetFind_Flagged()
    {
        var src = EfStubs + @"
public class Repo {
    private Microsoft.EntityFrameworkCore.DbSet<string> _set = new();
    public string Get() => {|#0:_set.Find(""id"")|};
}";
        var expected = new DiagnosticResult("THEMIA104", DiagnosticSeverity.Warning).WithLocation(0);
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task DbSetFindAsync_Flagged()
    {
        var src = EfStubs + @"
using System.Threading.Tasks;
public class Repo {
    private Microsoft.EntityFrameworkCore.DbSet<string> _set = new();
    public ValueTask<string> Get() => {|#0:_set.FindAsync(""id"")|};
}";
        var expected = new DiagnosticResult("THEMIA104", DiagnosticSeverity.Warning).WithLocation(0);
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task SetGenericFind_Flagged()
    {
        var src = EfStubs + @"
public class Repo {
    private Microsoft.EntityFrameworkCore.DbContext _ctx = new();
    public string Get() => {|#0:_ctx.Set<string>().Find(""id"")|};
}";
        var expected = new DiagnosticResult("THEMIA104", DiagnosticSeverity.Warning).WithLocation(0);
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task GuardedContextFindAsync_NotFlagged()
    {
        // DbContext.FindAsync<T> is the guarded path (member of DbContext, not DbSet<T>).
        var src = EfStubs + @"
using System.Threading.Tasks;
public class Repo {
    private Microsoft.EntityFrameworkCore.DbContext _ctx = new();
    public ValueTask<string> Get() => _ctx.FindAsync<string>(""id"");
}";
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task UnrelatedFind_NotFlagged()
    {
        // A user type with its own Find is not DbSet<T> — not flagged.
        var src = @"
public class Cache { public string Find(string k) => k; }
public class Repo {
    private Cache _c = new();
    public string Get() => _c.Find(""id"");
}";
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task InDataLayerAssembly_NotFlagged()
    {
        var src = EfStubs + @"
public class Repo {
    private Microsoft.EntityFrameworkCore.DbSet<string> _set = new();
    public string Get() => _set.Find(""id"");
}";
        var test = new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src };
        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectAssemblyName(projectId, "Themia.Framework.Data.EFCore"));
        await test.RunAsync();
    }

    [Fact]
    public async Task Suppressed_NotFlagged()
    {
        var src = EfStubs + @"
public class Repo {
    private Microsoft.EntityFrameworkCore.DbSet<string> _set = new();
#pragma warning disable THEMIA104
    public string Get() => _set.Find(""id"");
#pragma warning restore THEMIA104
}";
        await new Verify<DbSetFindBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet test tests/Themia.Analyzers.Tests/Themia.Analyzers.Tests.csproj --filter DbSetFindBypassAnalyzerTests`
Expected: BUILD FAILURE — `DbSetFindBypassAnalyzer` does not exist yet.

- [ ] **Step 3: Implement the analyzer**

Create `src/tooling/Themia.Analyzers/DbSetFindBypassAnalyzer.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Themia.Analyzers.Diagnostics;

namespace Themia.Analyzers;

/// <summary>THEMIA104: flags DbSet&lt;T&gt;.Find/FindAsync, which bypasses ThemiaDbContext's tenant
/// post-check for already-tracked entities. Steers callers to DbContext.FindAsync&lt;T&gt;() /
/// IReadRepository.GetByIdAsync(). Silent inside the Themia.Framework.Data.* assemblies.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DbSetFindBypassAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.DbSetFindBypass];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (DataLayerScope.IsDataLayerAssembly(context.Compilation.AssemblyName))
            return;

        var dbSet = context.Compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1");
        if (dbSet is null)
            return; // EF Core not referenced — nothing to flag.

        context.RegisterOperationAction(ctx => Analyze(ctx, dbSet), OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol dbSet)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        if (method.Name is not ("Find" or "FindAsync"))
            return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType.OriginalDefinition, dbSet))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.DbSetFindBypass, context.Operation.Syntax.GetLocation()));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Themia.Analyzers.Tests/Themia.Analyzers.Tests.csproj --filter DbSetFindBypassAnalyzerTests`
Expected: Passed — 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/tooling/Themia.Analyzers/DbSetFindBypassAnalyzer.cs tests/Themia.Analyzers.Tests/DbSetFindBypassAnalyzerTests.cs
git commit -m "feat(analyzers): THEMIA104 flags DbSet.Find tenant-isolation bypass"
```

---

### Task 3: THEMIA103 — raw Dapper connection analyzer (TDD)

**Files:**
- Create: `tests/Themia.Analyzers.Tests/RawConnectionBypassAnalyzerTests.cs`
- Create: `src/tooling/Themia.Analyzers/RawConnectionBypassAnalyzer.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Themia.Analyzers.Tests/RawConnectionBypassAnalyzerTests.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;
using Xunit;

namespace Themia.Analyzers.Tests;

public class RawConnectionBypassAnalyzerTests
{
    // Minimal stubs mirroring the real IDapperConnectionContext + ITenantQueryFactory so the analyzer
    // binds the symbols by metadata name without referencing Themia.Framework.Data.Dapper.
    private const string DapperStubs = @"
namespace Themia.Framework.Data.Dapper.Connection {
    public interface IDapperConnectionContext {
        System.Threading.Tasks.Task<object> GetOpenConnectionAsync(System.Threading.CancellationToken ct);
        System.Threading.Tasks.Task<object> BeginTransactionAsync(System.Threading.CancellationToken ct);
    }
}
namespace Themia.Framework.Data.Dapper.Tenancy {
    public interface ITenantQueryFactory { object For<T>(); }
}";

    [Fact]
    public async Task GetOpenConnectionAsync_Flagged()
    {
        var src = DapperStubs + @"
using Themia.Framework.Data.Dapper.Connection;
using System.Threading;
using System.Threading.Tasks;
public class Service {
    private IDapperConnectionContext _ctx = null!;
    public Task<object> Get() => {|#0:_ctx.GetOpenConnectionAsync(CancellationToken.None)|};
}";
        var expected = new DiagnosticResult("THEMIA103", DiagnosticSeverity.Warning).WithLocation(0);
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src, ExpectedDiagnostics = { expected } }.RunAsync();
    }

    [Fact]
    public async Task BeginTransactionAsync_NotFlagged()
    {
        // Only the raw-connection getter is the isolation bypass; transaction control is the UoW's concern.
        var src = DapperStubs + @"
using Themia.Framework.Data.Dapper.Connection;
using System.Threading;
using System.Threading.Tasks;
public class Service {
    private IDapperConnectionContext _ctx = null!;
    public Task<object> Get() => _ctx.BeginTransactionAsync(CancellationToken.None);
}";
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task TenantQueryFactory_NotFlagged()
    {
        var src = DapperStubs + @"
using Themia.Framework.Data.Dapper.Tenancy;
public class Service {
    private ITenantQueryFactory _f = null!;
    public object Get() => _f.For<string>();
}";
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task UnrelatedGetOpenConnectionAsync_NotFlagged()
    {
        // A user type with the same method name is not the Themia interface — not flagged.
        var src = @"
using System.Threading;
using System.Threading.Tasks;
public class FakeCtx { public Task<object> GetOpenConnectionAsync(CancellationToken ct) => Task.FromResult<object>(null!); }
public class Service {
    private FakeCtx _ctx = new();
    public Task<object> Get() => _ctx.GetOpenConnectionAsync(CancellationToken.None);
}";
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }

    [Fact]
    public async Task InDataLayerAssembly_NotFlagged()
    {
        var src = DapperStubs + @"
using Themia.Framework.Data.Dapper.Connection;
using System.Threading;
using System.Threading.Tasks;
public class Service {
    private IDapperConnectionContext _ctx = null!;
    public Task<object> Get() => _ctx.GetOpenConnectionAsync(CancellationToken.None);
}";
        var test = new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src };
        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectAssemblyName(projectId, "Themia.Framework.Data.Dapper"));
        await test.RunAsync();
    }

    [Fact]
    public async Task Suppressed_NotFlagged()
    {
        var src = DapperStubs + @"
using Themia.Framework.Data.Dapper.Connection;
using System.Threading;
using System.Threading.Tasks;
public class Service {
    private IDapperConnectionContext _ctx = null!;
#pragma warning disable THEMIA103
    public Task<object> Get() => _ctx.GetOpenConnectionAsync(CancellationToken.None);
#pragma warning restore THEMIA103
}";
        await new Verify<RawConnectionBypassAnalyzer>.Test { TestCode = src }.RunAsync();
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet test tests/Themia.Analyzers.Tests/Themia.Analyzers.Tests.csproj --filter RawConnectionBypassAnalyzerTests`
Expected: BUILD FAILURE — `RawConnectionBypassAnalyzer` does not exist yet.

- [ ] **Step 3: Implement the analyzer**

Create `src/tooling/Themia.Analyzers/RawConnectionBypassAnalyzer.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Themia.Analyzers.Diagnostics;

namespace Themia.Analyzers;

/// <summary>THEMIA103: flags IDapperConnectionContext.GetOpenConnectionAsync — the raw-connection bypass of
/// tenant isolation. Steers callers to ITenantQueryFactory.For&lt;T&gt;(). Silent inside the
/// Themia.Framework.Data.* assemblies.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RawConnectionBypassAnalyzer : DiagnosticAnalyzer
{
    private const string ConnectionContextMetadataName =
        "Themia.Framework.Data.Dapper.Connection.IDapperConnectionContext";

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [DiagnosticDescriptors.RawConnectionBypass];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (DataLayerScope.IsDataLayerAssembly(context.Compilation.AssemblyName))
            return;

        var contextType = context.Compilation.GetTypeByMetadataName(ConnectionContextMetadataName);
        if (contextType is null)
            return; // Dapper data layer not referenced.

        context.RegisterOperationAction(ctx => Analyze(ctx, contextType), OperationKind.Invocation);
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol contextType)
    {
        var method = ((IInvocationOperation)context.Operation).TargetMethod;
        if (method.Name != "GetOpenConnectionAsync")
            return;
        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, contextType))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.RawConnectionBypass, context.Operation.Syntax.GetLocation()));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Themia.Analyzers.Tests/Themia.Analyzers.Tests.csproj --filter RawConnectionBypassAnalyzerTests`
Expected: Passed — 6 tests.

- [ ] **Step 5: Run the whole analyzer test suite**

Run: `dotnet test tests/Themia.Analyzers.Tests/Themia.Analyzers.Tests.csproj`
Expected: Passed — all tests (existing 101/102 + new 103/104).

- [ ] **Step 6: Commit**

```bash
git add src/tooling/Themia.Analyzers/RawConnectionBypassAnalyzer.cs tests/Themia.Analyzers.Tests/RawConnectionBypassAnalyzerTests.cs
git commit -m "feat(analyzers): THEMIA103 flags raw Dapper connection bypass"
```

---

### Task 4: Release tracking, PublicAPI, help docs

**Files:**
- Modify: `src/tooling/Themia.Analyzers/AnalyzerReleases.Unshipped.md`
- Modify: `src/tooling/Themia.Analyzers/PublicAPI.Unshipped.txt`
- Create: `docs/analyzers/THEMIA103.md`, `docs/analyzers/THEMIA104.md`

- [ ] **Step 1: Add the rules to release tracking**

Append two rows to the `### New Rules` table in `src/tooling/Themia.Analyzers/AnalyzerReleases.Unshipped.md` (keep the existing 101/102 rows):

```
THEMIA103 | Themia.Isolation | Warning | Raw Dapper connection bypasses tenant isolation
THEMIA104 | Themia.Isolation | Warning | DbSet.Find bypasses the tenant post-check
```

- [ ] **Step 2: Add the public surface**

Append to `src/tooling/Themia.Analyzers/PublicAPI.Unshipped.txt` (the analyzer classes are public; their base members come from `DiagnosticAnalyzer`):

```
Themia.Analyzers.DbSetFindBypassAnalyzer
Themia.Analyzers.DbSetFindBypassAnalyzer.DbSetFindBypassAnalyzer() -> void
Themia.Analyzers.RawConnectionBypassAnalyzer
Themia.Analyzers.RawConnectionBypassAnalyzer.RawConnectionBypassAnalyzer() -> void
override Themia.Analyzers.DbSetFindBypassAnalyzer.SupportedDiagnostics.get -> System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.DiagnosticDescriptor!>
override Themia.Analyzers.DbSetFindBypassAnalyzer.Initialize(Microsoft.CodeAnalysis.Diagnostics.AnalysisContext! context) -> void
override Themia.Analyzers.RawConnectionBypassAnalyzer.SupportedDiagnostics.get -> System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.DiagnosticDescriptor!>
override Themia.Analyzers.RawConnectionBypassAnalyzer.Initialize(Microsoft.CodeAnalysis.Diagnostics.AnalysisContext! context) -> void
```

- [ ] **Step 3: Write the help docs**

Create `docs/analyzers/THEMIA103.md`:

```markdown
# THEMIA103 — Raw Dapper connection bypasses tenant isolation

**Category:** Themia.Isolation · **Severity:** Warning

`IDapperConnectionContext.GetOpenConnectionAsync` returns the raw, unscoped database connection. Queries
issued on it do not carry the tenant predicate or soft-delete filter, so they can read or write across
tenants — the isolation guarantee holds only when access flows through the repositories.

**Do this instead:** use `ITenantQueryFactory.For<T>()` for ad-hoc tenant-aware queries (it pre-seeds the
tenant predicate + soft-delete filter), or the repositories / unit of work for reads and writes.

**Deliberate bypass:** suppress with a justification — `#pragma warning disable THEMIA103` or
`[SuppressMessage("Themia.Isolation", "THEMIA103", Justification = "…")]`. The suppression makes the
bypass conspicuous and reviewable in the diff.
```

Create `docs/analyzers/THEMIA104.md`:

```markdown
# THEMIA104 — DbSet.Find bypasses the tenant post-check

**Category:** Themia.Isolation · **Severity:** Warning

`DbSet<T>.Find` / `FindAsync` returns an already-tracked entity straight from EF's identity map **without**
re-applying `ThemiaDbContext`'s tenant / soft-delete post-check. If a row from another tenant is already
tracked in the context, `DbSet.Find` will return it.

**Do this instead:** use `DbContext.FindAsync<T>()` (Themia's guarded override) or
`IReadRepository.GetByIdAsync()` — both re-validate tenant access even for tracked entities.

**Deliberate bypass:** suppress with a justification — `#pragma warning disable THEMIA104` or
`[SuppressMessage("Themia.Isolation", "THEMIA104", Justification = "…")]`.
```

- [ ] **Step 4: Clean build to confirm no RS0016 (undocumented public API)**

Run: `dotnet build src/tooling/Themia.Analyzers/Themia.Analyzers.csproj --no-incremental`
Expected: Build succeeded, 0 warnings. (If RS0016 fires, a public member is missing from `PublicAPI.Unshipped.txt` — add the exact line it names.)

- [ ] **Step 5: Commit**

```bash
git add src/tooling/Themia.Analyzers/AnalyzerReleases.Unshipped.md src/tooling/Themia.Analyzers/PublicAPI.Unshipped.txt docs/analyzers/
git commit -m "docs(analyzers): release tracking, PublicAPI, help docs for THEMIA103/104"
```

---

### Task 5: Dogfood the analyzer on Themia's own production code

**Files:**
- Create: `src/Directory.Build.props`
- Create: `src/tooling/Directory.Build.props`

Goal: apply the analyzer to Themia's `src/` production projects (excluding `src/tooling/` to avoid the analyzer referencing itself, and `tests/` which legitimately poke at internals). Rather than a fragile path-`Contains` condition, use MSBuild's `Directory.Build.props` resolution (each project imports the **nearest** ancestor): a `src/Directory.Build.props` adds the analyzer for everything under `src/`, and a `src/tooling/Directory.Build.props` shadows it for tooling so those projects skip it. Both must re-import the root `Directory.Build.props` to preserve its settings (Nullable, Version, CPM, etc.). The `Themia.Framework.Data.*` projects self-silence by assembly name; non-EF/Dapper projects no-op (the `GetTypeByMetadataName` guard returns null). Expected outcome: build stays green; any genuine hit is fixed or suppressed-with-justification.

- [ ] **Step 1: Add `src/Directory.Build.props` (imports root + adds the analyzer)**

Create `src/Directory.Build.props`:

```xml
<Project>
  <!-- Preserve the repo-root settings, then dogfood the Themia isolation analyzers on src/ code. -->
  <Import Project="$(MSBuildThisFileDirectory)../Directory.Build.props" />
  <ItemGroup>
    <ProjectReference Include="$(MSBuildThisFileDirectory)tooling/Themia.Analyzers/Themia.Analyzers.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false"
                      PrivateAssets="all" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add `src/tooling/Directory.Build.props` (imports root, no analyzer)**

Create `src/tooling/Directory.Build.props` so tooling projects (the analyzer itself + generators) do **not** reference the analyzer — this is the nearest ancestor for `src/tooling/*`, so it shadows `src/Directory.Build.props`:

```xml
<Project>
  <!-- Tooling projects skip the analyzer dogfood (an analyzer can't reference itself). -->
  <Import Project="$(MSBuildThisFileDirectory)../../Directory.Build.props" />
</Project>
```

- [ ] **Step 3: Build the whole solution and surface any hits**

Run: `dotnet build Themia.sln`
Expected: Build succeeded. If THEMIA103/104 fire (TreatWarningsAsErrors makes them errors), inspect each:
- A real bypass in a module → replace with the guarded API (`ITenantQueryFactory.For<T>()` / `DbContext.FindAsync<T>()` / `IReadRepository.GetByIdAsync()`).
- A legitimate case → `#pragma warning disable THEMIAxxx` with a one-line justification comment.
Re-run until green. (Most likely there are zero hits.) If the `src/` build instead fails with missing common settings — e.g. CS8632 nullable or a missing `<Version>` — the root re-import in Step 1/2 is wrong; verify the `Import` paths resolve to the repo-root `Directory.Build.props`.

- [ ] **Step 4: Confirm the analyzer actually ran (sanity)**

Temporarily add `_ = default(Microsoft.EntityFrameworkCore.DbSet<string>)!.Find("x");` to a throwaway method in `src/modules/Themia.Modules.Scheduling/SchedulingModule.cs`, build, and confirm THEMIA104 fires. Then revert the line. (Proves the dogfood wiring is live, not silently inert.)

Run: `dotnet build src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj`
Expected: error THEMIA104 on that line; after revert, Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/Directory.Build.props src/tooling/Directory.Build.props
git commit -m "build(analyzers): dogfood THEMIA103/104 on Themia src production projects"
```

---

### Task 6: Flow the analyzer to adopters of the data packages

**Files:**
- Modify: `src/tooling/Themia.Analyzers/Themia.Analyzers.csproj`
- Modify: `src/framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj`
- Modify: `src/framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj`
- Modify: `src/framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj`

Goal: adopters who reference a Themia data package receive THEMIA103/104 (and 101/102) transitively, the way `Microsoft.EntityFrameworkCore.Analyzers` reaches EF consumers.

- [ ] **Step 1: Make Themia.Analyzers a non-development dependency**

In `src/tooling/Themia.Analyzers/Themia.Analyzers.csproj`, remove the line:

```xml
<DevelopmentDependency>true</DevelopmentDependency>
```

(A development dependency is excluded from transitive package flow; dropping it lets the analyzer reach consumers of any package that depends on it.)

- [ ] **Step 2: Reference the analyzer from each data package so it packs as a dependency**

To each of the three data-package csprojs, add the analyzer reference inside a new `ItemGroup`. `OutputItemType="Analyzer"` applies it locally (harmless — the data layer self-silences); on `dotnet pack`, a `ProjectReference` to a packable project (Themia.Analyzers has a `PackageId`) becomes a NuGet `<dependency>`:

```xml
<ItemGroup>
  <!-- Flow the Themia isolation analyzers to adopters of this data package. -->
  <ProjectReference Include="$(MSBuildThisFileDirectory)../../tooling/Themia.Analyzers/Themia.Analyzers.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

Add it to `Themia.Framework.Data.Abstractions`, `Themia.Framework.Data.Dapper`, and `Themia.Framework.Data.EFCore`. (Do **not** set `PrivateAssets="all"` here — that would block the transitive flow we want.)

- [ ] **Step 3: Build to confirm nothing broke**

Run: `dotnet build Themia.sln`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 4: Pack a data package and assert the dependency is present**

Run: `dotnet pack src/framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj -o /tmp/themia-pack`
Then inspect the produced nuspec:
Run: `unzip -p /tmp/themia-pack/Themia.Framework.Data.EFCore.0.4.9.nupkg '*.nuspec' | grep -i analyzers`
Expected: a `<dependency id="Themia.Analyzers" .../>` line appears (not under a `developmentDependency`/excluded group).

If it does **not** appear, apply the FALLBACK: in each data package, instead of relying on the dependency, bundle the analyzer DLLs into the package's own `analyzers/dotnet/cs` (deterministic, mirrors `Themia.Analyzers.csproj`'s own `PackAnalyzerDlls` target). Add to each data csproj:

```xml
<PropertyGroup>
  <TargetsForTfmSpecificContentInPackage>$(TargetsForTfmSpecificContentInPackage);PackThemiaAnalyzers</TargetsForTfmSpecificContentInPackage>
</PropertyGroup>
<Target Name="PackThemiaAnalyzers" DependsOnTargets="ResolveReferences">
  <ItemGroup>
    <TfmSpecificPackageFile Include="$(MSBuildThisFileDirectory)../../tooling/Themia.Analyzers/bin/$(Configuration)/netstandard2.0/Themia.Analyzers.dll">
      <PackagePath>analyzers/dotnet/cs</PackagePath>
    </TfmSpecificPackageFile>
    <TfmSpecificPackageFile Include="$(MSBuildThisFileDirectory)../../tooling/Themia.Analyzers/bin/$(Configuration)/netstandard2.0/Themia.Generators.Abstractions.dll">
      <PackagePath>analyzers/dotnet/cs</PackagePath>
    </TfmSpecificPackageFile>
  </ItemGroup>
</Target>
```

(Use the dependency approach if Step 4 confirms it works — single source, no duplicate-analyzer warnings when an adopter references two data packages. Only fall back to bundling if the dependency does not flow.)

- [ ] **Step 5: Prove adopter reach with a throwaway consumer**

Create a scratch consumer that references the packed package and confirm the warning fires:

```bash
mkdir -p /tmp/themia-consumer && cd /tmp/themia-consumer
cat > consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Themia.Framework.Data.EFCore" Version="0.4.9" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
  </ItemGroup>
</Project>
EOF
cat > Program.cs <<'EOF'
using Microsoft.EntityFrameworkCore;
var set = default(DbSet<string>)!;
_ = set.Find("x");
EOF
dotnet build -p:RestoreAdditionalProjectSources=/tmp/themia-pack 2>&1 | grep THEMIA104
```
Expected: a `warning THEMIA104` line. Then `cd - && rm -rf /tmp/themia-consumer /tmp/themia-pack`.

(If `dotnet build` can't resolve the local feed, add `/tmp/themia-pack` as a source via `dotnet nuget add source /tmp/themia-pack -n themia-local` first, and remove it after.)

- [ ] **Step 6: Commit**

```bash
git add src/tooling/Themia.Analyzers/Themia.Analyzers.csproj src/framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj src/framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj src/framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj
git commit -m "build(analyzers): flow Themia.Analyzers to adopters of the data packages"
```

---

### Task 7: Release — version bump, CHANGELOG, MIGRATION

**Files:**
- Modify: `Directory.Build.props` (`<Version>`)
- Modify: `CHANGELOG.md`
- Modify: `MIGRATION.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>0.4.8</Version>` to `<Version>0.4.9</Version>`.

- [ ] **Step 2: CHANGELOG**

Under `## [Unreleased]` in `CHANGELOG.md`, add a `## 0.4.9 — 2026-06-13` section:

```markdown
## 0.4.9 — 2026-06-13

### Added

- **Tenant-isolation analyzers (THEMIA103/104).** `Themia.Analyzers` now ships two build-time rules
  (category `Themia.Isolation`, Warning) closing DECISION #6's by-construction gap: **THEMIA103** flags
  raw Dapper connection access (`IDapperConnectionContext.GetOpenConnectionAsync`), steering to
  `ITenantQueryFactory.For<T>()`; **THEMIA104** flags `DbSet<T>.Find/FindAsync`, which bypasses
  `ThemiaDbContext`'s tenant post-check for already-tracked entities, steering to `DbContext.FindAsync<T>()`
  / `IReadRepository.GetByIdAsync()`. Both stay silent inside the `Themia.Framework.Data.*` assemblies and
  fire everywhere else. Deliberate bypasses use standard suppression (`#pragma`/`[SuppressMessage]`).

### Changed

- **`Themia.Analyzers` now flows to consumers of the `Themia.Framework.Data.*` packages.** Adopters of a
  Themia data package will see Themia analyzer warnings — the new isolation gates plus the pre-existing
  THEMIA101 (catch-log-rethrow) / THEMIA102 (sync-over-async) hygiene rules. Configure severity or suppress
  per `.editorconfig`. See [MIGRATION.md](MIGRATION.md).
```

- [ ] **Step 3: MIGRATION**

Add a `## 0.4.9` section at the top of `MIGRATION.md` (newest-first):

```markdown
## 0.4.9

### Themia analyzers now run in adopter builds

**What changed:** referencing any `Themia.Framework.Data.*` package now brings the `Themia.Analyzers`
rules into your build: THEMIA103/104 (tenant-isolation gates) and the pre-existing THEMIA101/102 hygiene
rules. They are **Warnings**, not errors.

**Why:** DECISION #6 — tenant isolation should hold by construction. The two gates flag the raw-connection
and `DbSet.Find` bypasses at build time so the safe path is inescapable without an explicit, reviewable
suppression.

**How to upgrade:**

- No action required if you build with warnings as warnings.
- To silence a rule globally, add to `.editorconfig`: `dotnet_diagnostic.THEMIA104.severity = none`
  (or `= error` to enforce it harder), or configure the whole group via
  `dotnet_analyzer_diagnostic.category-Themia.Isolation.severity = …`.
- For a one-off deliberate bypass, suppress at the call site with a justification:
  `#pragma warning disable THEMIA103` or `[SuppressMessage("Themia.Isolation", "THEMIA103", Justification = "…")]`.
- The guarded alternatives are `ITenantQueryFactory.For<T>()` (Dapper) and `DbContext.FindAsync<T>()` /
  `IReadRepository.GetByIdAsync()` (EF).
```

- [ ] **Step 4: Full clean build + test**

Run: `dotnet build Themia.sln --no-incremental && dotnet test tests/Themia.Analyzers.Tests/Themia.Analyzers.Tests.csproj`
Expected: Build succeeded 0 warnings; analyzer tests all pass.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props CHANGELOG.md MIGRATION.md
git commit -m "release: Themia 0.4.9 — tenant-isolation analyzer gates"
```

---

## Notes for the implementer

- **Do not** add a code-fix provider or a custom bypass-marker API — out of scope (spec § Out of scope).
- **Do not** split THEMIA101/102 into a separate assembly — they ride along to adopters by design.
- The reported diagnostic location is the whole invocation expression; test markers `{|#0:…|}` must wrap
  the full call (e.g. `{|#0:_set.Find("id")|}`), matching the examples above.
- If the analyzer test harness reports a *compiler* diagnostic (CS error) you didn't expect, the inline
  stub is probably missing a member — extend the stub; do not add `DiagnosticResult`s for compiler errors.
- Task 6 Step 4/5 is the one empirically-verified part: trust the pack-and-consume result over intuition
  about NuGet analyzer flow.
