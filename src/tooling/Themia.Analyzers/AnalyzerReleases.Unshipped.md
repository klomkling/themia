; New rules for the next release of Themia.Analyzers.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
THEMIA101 | Themia.DI | Warning | Catch logs and rethrows
THEMIA102 | Themia.DI | Warning | Synchronous work wrapped in Task.FromResult
THEMIA103 | Themia.Isolation | Warning | Raw Dapper connection bypasses tenant isolation
THEMIA104 | Themia.Isolation | Warning | DbSet.Find bypasses the tenant post-check
