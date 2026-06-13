# Follow-ups: tenant-isolation analyzers (deferred from the 0.4.9 final review)

Two findings from the whole-branch review of the 0.4.9 isolation analyzers (THEMIA103/104) were judged
real but non-blocking and out of scope for 0.4.9. Captured here so they aren't lost. Neither is a runtime
vulnerability — the analyzers are build-time guard-rails; tenant isolation is still enforced at runtime by
the repositories / EF query filters regardless of whether the analyzer fires.

## 1. `GetTypeByMetadataName` silently no-ops if the target type is multiply-defined — DONE (0.4.9)

**Resolved in 0.4.9.** Both analyzers (`RawConnectionBypassAnalyzer`, `DbSetFindBypassAnalyzer`) now resolve
their target via `Compilation.GetTypesByMetadataName(...)` (plural) in `CompilationStart`, register the
operation action when the result set is non-empty, and match the invocation's containing type against any
of the resolved symbols. The singular `GetTypeByMetadataName` returned `null` (silently disengaging the
gate) when the type was defined in more than one referenced assembly; the plural overload handles that. A
missed warning was never a broken isolation guarantee — runtime isolation is enforced by the repositories /
EF filters regardless — but for a security gate, robust matching is worth it.

## 2. CI smoke-test for transitive analyzer flow to adopters — DONE (0.4.9)

**Resolved in 0.4.9.** `eng/verify-analyzer-flow.sh` packs the solution to a local feed, runs structural
checks (the EFCore nuspec depends on `Themia.Analyzers` without excluding the Analyzers asset; both DLLs are
co-located in `analyzers/dotnet/cs`), then builds a throwaway consumer of `Themia.Framework.Data.EFCore` with
a `DbSet.Find` call and asserts THEMIA104 fires. Wired into `.github/workflows/ci.yml` as the `analyzer-flow`
job, so a future packaging change that breaks adopter reach (re-adding `DevelopmentDependency`, flipping an
asset flag, breaking `PackAnalyzerDlls`) now turns CI red.

## Not pursued (intentional, 0.4.9)

- Receiver-type / subclass-walking detection for THEMIA104 (overriding `DbSet<T>` subclass) and concrete-type
  detection for THEMIA103 — both documented in-code as benign limitations (`DbSet<T>` is EF-supplied;
  `DapperConnectionContext` is `internal sealed`, so adopters can't hold a concrete reference). YAGNI.
- No code-fix provider / custom bypass-marker API (standard suppression suffices).
