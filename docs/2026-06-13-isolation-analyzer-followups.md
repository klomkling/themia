# Follow-ups: tenant-isolation analyzers (deferred from the 0.4.9 final review)

Two findings from the whole-branch review of the 0.4.9 isolation analyzers (THEMIA103/104) were judged
real but non-blocking and out of scope for 0.4.9. Captured here so they aren't lost. Neither is a runtime
vulnerability — the analyzers are build-time guard-rails; tenant isolation is still enforced at runtime by
the repositories / EF query filters regardless of whether the analyzer fires.

## 1. `GetTypeByMetadataName` silently no-ops if the target type is multiply-defined

**Where:** `src/tooling/Themia.Analyzers/RawConnectionBypassAnalyzer.cs` and `DbSetFindBypassAnalyzer.cs`,
the `CompilationStart` symbol resolution.

Both analyzers resolve their target (`Microsoft.EntityFrameworkCore.DbSet`1`,
`Themia.Framework.Data.Dapper.Connection.IDapperConnectionContext`) via `Compilation.GetTypeByMetadataName`,
which returns `null` when the type is defined in **more than one** referenced assembly (a documented Roslyn
behavior). On `null` the analyzer returns early and the gate goes quiet for that compilation.

**Risk if untouched:** in an unusual graph that pulls the same type from two assemblies (e.g. two EF Core
versions unified oddly), the gate silently disengages — a missed *warning*, not a broken isolation
guarantee. Very rare for these specific types (`DbSet<T>` is single-sourced under normal NuGet unification;
`IDapperConnectionContext` is a single Themia assembly).

**Suggested fix:** switch to `Compilation.GetTypesByMetadataName(...)` (plural) and register the operation
action if the result set is non-empty, comparing the invocation's containing type against any of them.
Converts a silent no-op into robust matching.

## 2. No CI smoke-test for transitive analyzer flow to adopters

**Where:** the packaging in `src/framework/Themia.Framework.Data.*` csprojs + `Themia.Analyzers.csproj`.

The adopter-flow path (a consumer of a `Themia.Framework.Data.*` package receives THEMIA103/104
transitively) is verified by inspecting the packed nuspec + the analyzer package's co-located DLLs, and the
dogfood proves the analyzer loads and fires. But there is no **automated** consumer-compilation test, so a
future packaging change (e.g. someone re-adds `DevelopmentDependency`, or flips an asset flag) could
silently break adopter reach without any test going red.

**Suggested fix:** a tiny CI step that packs the data packages + `Themia.Analyzers` to a local feed, builds a
throwaway consumer project referencing one data package with a `DbSet.Find` call, and asserts a THEMIA104
warning is emitted. Makes transitive analyzer flow regression-proof.

## Not pursued (intentional, 0.4.9)

- Receiver-type / subclass-walking detection for THEMIA104 (overriding `DbSet<T>` subclass) and concrete-type
  detection for THEMIA103 — both documented in-code as benign limitations (`DbSet<T>` is EF-supplied;
  `DapperConnectionContext` is `internal sealed`, so adopters can't hold a concrete reference). YAGNI.
- No code-fix provider / custom bypass-marker API (standard suppression suffices).
