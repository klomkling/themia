# Themia 0.4.9 — Tenant-isolation analyzer gates (design)

**Status:** approved (brainstorm) — 2026-06-13
**Release:** 0.4.9 (single-version monorepo)
**Package touched:** `Themia.Analyzers` (+ packaging changes to the `Themia.Framework.Data.*` packages)

## Goal

Close the last "tenant isolation by convention, not by construction" gap from **DECISION #6** with two
build-time Roslyn rules that steer callers off the two unguarded bypasses and onto the guarded APIs:

- **THEMIA103** — flags raw Dapper connection access (`IDapperConnectionContext.GetOpenConnectionAsync`).
- **THEMIA104** — flags `DbSet<T>.Find` / `DbSet<T>.FindAsync` (bypasses `ThemiaDbContext`'s tenant
  post-check for already-tracked entities).

Both ship to **adopters** of the Themia data packages as **Warnings**, with **standard Roslyn suppression**
as the escape hatch and **no code fix**.

## Background (DECISION #6, verbatim acceptance criteria)

From `docs/themia-architecture-overview.md` (DECISION #6): tenant isolation must hold *by construction*.
EF enforces it via model-level query filters (default-safe); Dapper enforces it by convention (only when
access flows through the repositories). Two structural holes remain:

1. **Dapper raw connection** — `IDapperConnectionContext.GetOpenConnectionAsync` is an ambient, unguarded
   bypass. The sanctioned path for ad-hoc tenant-aware queries is `ITenantQueryFactory.For<T>()` (pre-seeds
   the tenant predicate + soft-delete filter). Acceptance criterion (c): *"a `Themia.Analyzers` build-time
   rule flags raw-connection use outside the data-access assembly."*
2. **EF `DbSet.Find` residual** — `DbSet<T>.Find/FindAsync` returns an already-tracked entity **without**
   re-applying `ThemiaDbContext`'s tenant/soft-delete post-check (EF identity-map semantics; see
   `docs/2026-06-11-efcore-sqlserver-find-isolation-issue.md`). The guarded paths are
   `DbContext.FindAsync<T>()` and `EfReadRepository.GetByIdAsync()`. DECISION #6: *"flag direct
   `DbSet.Find*` and `Set<T>().Find*` calls outside the data layer, steering callers to the guarded APIs."*

## The exact symbols

| Concern | Type / member | Assembly / namespace |
|---|---|---|
| Gate A target | `IDapperConnectionContext.GetOpenConnectionAsync(CancellationToken)` | `Themia.Framework.Data.Dapper` / `…Dapper.Connection` |
| Gate A guarded path | `ITenantQueryFactory.For<T>()` | `Themia.Framework.Data.Dapper` / `…Dapper.Tenancy` |
| Gate B target | `DbSet<T>.Find(…)` / `DbSet<T>.FindAsync(…)` | `Microsoft.EntityFrameworkCore.DbSet`1` |
| Gate B guarded paths | `ThemiaDbContext.FindAsync<T>(…)` / `Find<T>(…)`; `EfReadRepository<T,TKey>.GetByIdAsync(…)` (→ `IReadRepository`) | `Themia.Framework.Data.EFCore` |
| Gate B post-check | `ThemiaDbContext.ValidateTenantAccess<TEntity>(TEntity?)` | `Themia.Framework.Data.EFCore` |

## Architecture

Two new `DiagnosticAnalyzer` classes in the existing `Themia.Analyzers` (`netstandard2.0`,
`src/tooling/Themia.Analyzers`):

- `RawConnectionBypassAnalyzer` → **THEMIA103**
- `DbSetFindBypassAnalyzer` → **THEMIA104**

### Detection (symbol-based, robust)

Each analyzer uses a `CompilationStartAction` so it resolves its target symbol **once** and only registers
the per-invocation action when the target type is referenced (zero false positives in projects that don't
use Dapper / EF, and zero cost there):

- **THEMIA103:** in `CompilationStart`, resolve `IDapperConnectionContext` via
  `compilation.GetTypeByMetadataName("Themia.Framework.Data.Dapper.Connection.IDapperConnectionContext")`.
  If `null`, return (Dapper not referenced). Otherwise register an `OperationAction` for
  `OperationKind.Invocation`; flag when `invocation.TargetMethod.Name == "GetOpenConnectionAsync"` **and**
  `SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, idapperConnectionContextSymbol)`.
- **THEMIA104:** in `CompilationStart`, resolve `DbSet<T>` via
  `compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.DbSet`1")`. If `null`, return (EF not
  referenced). Otherwise register an `OperationAction` for `OperationKind.Invocation`; flag when
  `invocation.TargetMethod.Name is "Find" or "FindAsync"` **and**
  `SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType.OriginalDefinition, dbSetOpenGenericSymbol)`.
  This matches both `dbSet.Find(…)` and `context.Set<T>().Find(…)` (both invoke a member of `DbSet<T>`), and
  does **not** match the guarded `DbContext.FindAsync<T>()` (member of `DbContext`, a different type).

Report the diagnostic at the invocation's syntax location.

### Scope: silent inside the data layer

Both analyzers consult a shared helper (new file `src/tooling/Themia.Analyzers/DataLayerScope.cs`):

```csharp
internal static class DataLayerScope
{
    // The data-access assemblies legitimately own the raw primitives (repositories, connection context,
    // the guarded ThemiaDbContext.Find overrides that call base.Find). Everything else — adopter code AND
    // Themia.Modules.* — must use the guarded APIs.
    public static bool IsDataLayerAssembly(string? assemblyName) =>
        assemblyName is not null &&
        assemblyName.StartsWith("Themia.Framework.Data", System.StringComparison.Ordinal);
}
```

In each analyzer's `CompilationStart`, if `IsDataLayerAssembly(context.Compilation.AssemblyName)` is true,
return without registering the invocation action. Net effect:

- `Themia.Framework.Data.Abstractions` / `.Dapper` / `.Dapper.<engine>` / `.EFCore` / `.EFCore.<engine>` →
  **silent** (they own the bypasses).
- Adopter code, and Themia's own `Themia.Modules.*` / app code → **gated** (Themia dogfoods the gate).

### Diagnostic descriptors

`CreateWarning` in `Themia.Generators.Abstractions` hardcodes category `Themia.DI` and takes only
`(id, title, messageFormat)`. To give the isolation gates their own configurable category **without**
disturbing that shared helper's shipped public API, build the two descriptors locally in
`src/tooling/Themia.Analyzers/Diagnostics/DiagnosticDescriptors.cs`:

```csharp
private const string IsolationCategory = "Themia.Isolation";

private static DiagnosticDescriptor IsolationWarning(string id, string title, string message) =>
    new(id, title, message, IsolationCategory, DiagnosticSeverity.Warning, isEnabledByDefault: true,
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
    "DbContext.FindAsync<T>() or IReadRepository.GetByIdAsync(). Suppress THEMIA104 with a justification " +
    "for a deliberate bypass.");
```

(The `helpLinkUri` points at short per-rule docs added under `docs/analyzers/THEMIA103.md` /
`THEMIA104.md` — one paragraph each: what it flags, why, the guarded alternative, how to suppress.)

### Escape hatch

Standard Roslyn suppression only — `#pragma warning disable THEMIA103` or
`[SuppressMessage("Themia.Isolation", "THEMIA103:…", Justification = "…")]`. No custom marker API. The
suppression is the conspicuous, reviewable bypass DECISION #6 (b) calls for.

## Packaging & reach (the riskiest part)

Today `Themia.Analyzers` is `DevelopmentDependency=true` and referenced by nothing but its own test
project — it runs nowhere. Two wiring changes:

1. **Dogfood inside Themia.** Reference `Themia.Analyzers` from the repo-root build so Themia's own
   projects are analyzed. Add to `Directory.Build.props` (or a shared `*.props`) a `ProjectReference` to
   `Themia.Analyzers` with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"` (and likewise for
   its `Themia.Generators.Abstractions` dependency, already wired). This gates `Themia.Modules.*` and app
   code; the `Themia.Framework.Data.*` assemblies self-suppress by name.

2. **Flow to adopters.** Make `Themia.Analyzers` a *normal* analyzer dependency of the data packages —
   mirroring how `Microsoft.EntityFrameworkCore.Analyzers` reaches EF consumers — so any adopter of a
   Themia data package gets THEMIA103/104 transitively. Concretely:
   - Drop `DevelopmentDependency=true` from `Themia.Analyzers.csproj` (a dev-dependency is excluded from
     transitive flow).
   - `Themia.Framework.Data.Abstractions`, `Themia.Framework.Data.Dapper`, and
     `Themia.Framework.Data.EFCore` reference `Themia.Analyzers` so the analyzer assets flow transitively
     (no `PrivateAssets=all`; `analyzers` included). **As implemented (supersedes the original
     `PackageReference` + CPM-pin note):** a `ProjectReference` with `<PrivateAssets>none</PrivateAssets>`
     + `<IncludeAssets>analyzers</IncludeAssets>` — on `dotnet pack` this renders the nuspec dependency
     `<dependency id="Themia.Analyzers" exclude="Runtime,Compile,Build,Native,BuildTransitive" />`
     (analyzers flow, nothing else). A ProjectReference avoids a CPM version pin and keeps the dependency
     in lockstep with the single-version monorepo; `release.yml` packs the whole solution so
     `Themia.Analyzers` publishes in the same batch.
   - **Consequence (approved):** because one analyzer assembly is all-or-nothing, adopters also receive the
     existing **THEMIA101/102** (benign hygiene Warnings, category `Themia.DI`, suppressible via
     `.editorconfig`). No second analyzer package — YAGNI.

   **Verification (the plan MUST include this):** `dotnet pack` a data package and assert the resulting
   `.nuspec` carries a non-development `Themia.Analyzers` dependency, and that a minimal sample-consumer
   compilation referencing the packed data package reports THEMIA103/104. (Transitive analyzer flow is
   easy to get subtly wrong; a green unit test of the analyzer alone does not prove adopter reach.)

## Testing

Follow the existing `tests/Themia.Analyzers.Tests` harness (`Verify<TAnalyzer>.Test :
CSharpAnalyzerTest<TAnalyzer, XUnitVerifier>`). Per gate:

- **Positive** — a call to the bypass in an ordinary (`TestProject`) assembly is flagged at the right span.
- **Negative (data layer)** — the same call is **not** flagged when the test compilation's assembly name
  starts with `Themia.Framework.Data`, set via a `SolutionTransform`:
  `test.SolutionTransforms.Add((sln, projId) => sln.WithProjectAssemblyName(projId, "Themia.Framework.Data.Dapper"));`
- **Negative (guarded API)** — `ITenantQueryFactory.For<T>()` (Gate A) and `DbContext.FindAsync<T>()` /
  `IReadRepository.GetByIdAsync()` (Gate B) are **not** flagged.
- **Negative (type absent)** — a project not referencing Dapper / EF produces no diagnostic (the
  `GetTypeByMetadataName` guard returns early).
- **Suppression** — `#pragma warning disable` around the call suppresses the diagnostic.

Test fixtures provide minimal stub declarations of `IDapperConnectionContext` /
`Microsoft.EntityFrameworkCore.DbSet<T>` (and the guarded members) so the harness can bind the symbols
without referencing the full framework packages.

## Release & tracking

- Bump `Directory.Build.props` `<Version>` → **0.4.9**.
- `AnalyzerReleases.Unshipped.md` (Themia.Analyzers): add THEMIA103/104 rows (category `Themia.Isolation`,
  Warning). (THEMIA101/102 remain in Unshipped — out of scope to "ship" them in the release-tracking sense.)
- `PublicAPI.Unshipped.txt` (Themia.Analyzers): the two new public analyzer types + the public descriptor
  fields, as RS0016 requires.
- **CHANGELOG** — **Added**: the two isolation analyzer rules. **Changed**: `Themia.Analyzers` now flows to
  consumers of the `Themia.Framework.Data.*` packages (adopters will see Themia analyzer warnings,
  including the pre-existing THEMIA101/102).
- **MIGRATION.md** — a `0.4.9` note: adopters of the data packages will see new analyzer **warnings**
  (not errors); how to set severity / suppress per `.editorconfig`; the guarded alternatives.

## Out of scope (YAGNI)

- No code-fix provider / `Themia.Analyzers.CodeFixes` package (diagnostic message names the alternative).
- No custom bypass-marker API (standard suppression suffices).
- THEMIA103 does **not** flag `IDapperConnectionContext.BeginTransactionAsync` / `CurrentTransaction`
  (transaction boundary is the UoW's concern, not a raw-connection isolation bypass).
- No splitting THEMIA101/102 into a separate analyzer assembly to keep them internal — approved to let
  them ride along to adopters.

## Build sequence (for the plan)

1. Descriptors (`DiagnosticDescriptors.cs`) + `DataLayerScope.cs`.
2. `DbSetFindBypassAnalyzer` (THEMIA104) — TDD against the test harness.
3. `RawConnectionBypassAnalyzer` (THEMIA103) — TDD.
4. Per-rule help docs under `docs/analyzers/`.
5. Dogfood wiring (`Directory.Build.props`) — fix or suppress any THEMIA103/104 hits surfaced in
   `Themia.Modules.*` (expected: none, or justified suppressions).
6. Adopter-flow packaging (drop `DevelopmentDependency`; data-package references; central version) +
   pack-and-consume verification.
7. AnalyzerReleases / PublicAPI / CHANGELOG / MIGRATION / version bump.
