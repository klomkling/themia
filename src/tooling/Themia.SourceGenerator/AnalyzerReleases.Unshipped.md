; New rules for the next release of Themia.SourceGenerator.
; See https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|--------------------
THEMIA001 | Themia.DI | Error | Multiple lifetime attributes.
THEMIA002 | Themia.DI | Error | Multiple lifetime marker interfaces.
THEMIA003 | Themia.DI | Error | Attribute and marker lifetime disagree.
THEMIA004 | Themia.DI | Warning | Redundant lifetime attribute and marker.
THEMIA005 | Themia.DI | Warning | Ambiguous service type.
THEMIA006 | Themia.DI | Warning | Cannot register type.
THEMIA007 | Themia.DI | Error | Attribute service type conflicts with generic marker.
THEMIA008 | Themia.DI | Error | Registrar missing public constructor.
THEMIA009 | Themia.DI | Warning | Registrar is internal.
THEMIA010 | Themia.DI | Error | Legacy attribute usage.
