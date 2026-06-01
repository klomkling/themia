# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

This package is **design-phase**: it contains specs and an implementation plan under `docs/`, but
**no code, no `Themia.sln`, no projects yet**. The first artifact to build is `Themia.AspNetCore`,
fully specified task-by-task in `docs/superpowers/plans/2026-06-01-themia-aspnetcore.md`. Read
`docs/themia-architecture-overview.md` first — it is the master document every spec references.

When implementing, follow the plan's TDD flow (failing test → implement → pass → commit) and the
superpowers `executing-plans` / `subagent-driven-development` skills it calls out.

## What Themia is

Themia is a .NET 8/10 application framework — a **framework core** plus a catalog of **pluggable
modules** (`IThemiaModule`), rebranded from the in-repo `zenity`/`zenity-v2` and assembled by
merging four existing codebases (Zenity, ezy-assets, PowerACC, Idevs.Net.CoreLib). All packages
ship under the `Themia.*` NuGet prefix.

## Architecture (big picture)

Layered, with a strict dependency direction (lower layers never depend on higher ones):

```
Tooling (build-time, netstandard2.0)   Themia.SourceGenerator | Themia.Analyzers(.CodeFixes) | Themia.Generators.Abstractions
Framework core (net10.0)               Themia.Framework.Core | .Data.EFCore | .AspNetCore | Themia.MultiTenancy
                                       Themia.Mediator | Themia.Caching | Themia.Logging | Themia.Services
Neutral cores (net8.0;net10.0)         Themia.Quartz | Themia.Exceptional(.SqlServer/.MySql/.PostgreSql) | Themia.AspNetCore
Modules (net10.0)                      Themia.Modules.* (Scheduling, ExceptionLogging, Identity, Storage, …)
```

Two structural rules drive nearly every design choice:

1. **Framework / app boundary (scope guard).** Only framework + cross-cutting infrastructure
   enters Themia. Business domains (ezy-assets' Billing/CRM/Inventory, PowerACC's accounting) stay
   in their apps. When deciding whether something belongs in Themia, ask "is this cross-cutting
   infra or app domain?" — domain stays out.

2. **Three-layer pattern for cross-cutting concerns:** neutral core (`Themia.X`, no framework
   dependency) → Themia module (`Themia.Modules.X`, `IThemiaModule`, tenant-aware, EF-backed) →
   **[deferred]** Serenity adapter (`Idevs.Net.CoreLib.X`). The neutral core is kept Serenity-free
   to preserve PowerACC reuse, **but PowerACC is never a design driver** — the Serenity adapter is
   built only if/when PowerACC actually migrates (YAGNI).

## Target-framework policy (non-negotiable)

- **Neutral cores + cross-framework `Themia.*`** (`Themia.Quartz`, `Themia.Exceptional.*`,
  `Themia.AspNetCore`) → `net8.0;net10.0`. The **net8 leg is mandatory** — PowerACC (net8) cannot
  reference net10-only packages, and net10-only would kill the reuse option.
- **`Themia.Framework.*` + `Themia.Modules.*`** → `net10.0`.
- **Tooling** (analyzers/source generators) → `netstandard2.0` (Roslyn, build-time).

## Key resolved decisions (don't relitigate)

- **Data layer:** `Themia.Framework.Data.EFCore` (EF Core) is the **canonical, default** layer — it
  centrally enforces tenant isolation (global query filters) + audit + UoW across SQL Server, MySQL,
  and PostgreSQL. Raw Dapper is allowed **only** as a controlled read-only escape-hatch that shares
  EF's connection/transaction and forces the tenant predicate. Start EF-only; open the hatch only
  with profiling data — raw Dapper risks tenant-isolation bypass.
- **Multi-DB Phase 1:** SQL Server, MySQL (incl. MariaDB), PostgreSQL via a dialect strategy +
  per-provider packages.
- **Exception logging engine** is a custom Dapper store with an `IExceptionalSqlDialect` strategy
  and **one schema across all three engines** — *not* built on StackExchange.Exceptional's store.
- **Typed exceptions + ProblemDetails middleware live in `Themia.AspNetCore`**, NOT in
  `Themia.Exceptional` (capture/persist/dashboard only). Exceptions are **HTTP-agnostic** (no
  `StatusCode` property) — the middleware owns the single type→status map.
- **Sequences / document numbering:** port `ISequenceProvider` into `Themia.Framework.Data`,
  tenant-scoped (`(TenantId, SequenceKey)` PK), table-based (not `CREATE SEQUENCE` — MySQL 8 lacks
  it), keeping the **separate-transaction allocation** semantic (survives outer rollback; gaps OK,
  dups catastrophic).
- **Tooling moves to Themia** as one build-time family, merging Idevs `.Generators`/`.CodeFixes`
  with Zenity's mediator source-gen; rename diagnostic IDs `IDEVSGEN1xx → THEMIA1xx`.

## Build & test (once the solution is scaffolded)

The `Directory.Build.props` set by Task 1 of the plan enforces `Nullable`, `ImplicitUsings`,
`TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true`, and central package management
(`Directory.Packages.props`). Treat warnings as build failures.

```bash
# from Packages/themia/
dotnet build Themia.sln                              # builds all TFMs (net8.0 + net10.0)
dotnet build Themia.sln --no-incremental             # clean build (use to surface RS0016 PublicAPI diagnostics)
dotnet test Themia.sln                               # all tests, all TFMs
dotnet test Themia.sln --filter ProblemDetailsMiddlewareTests   # single test class
dotnet test Themia.sln --filter "FullyQualifiedName~Maps_domain_exception"  # single test
```

Cross-cutting packages track a **PublicAPI** surface (`PublicAPI.Shipped.txt` /
`PublicAPI.Unshipped.txt`); a clean build reports undocumented public members as `RS0016`.

## Conventions specific to this codebase

- Use `System.Text.Json` only — **never introduce `Newtonsoft.Json`**.
- Log via `ILogger<T>` only — no `Console.Error.WriteLine` (a deliberate fix vs. the ezy-assets
  source the code is ported from).
- Avoid double-logging in middleware/pipeline catch clauses — log once per handled exception.
- New cross-cutting code follows: neutral core (no framework dep) first, then the module wrapper —
  not the other way around.
```
