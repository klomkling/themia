# Themia — Architecture Overview & Module Catalog

> Master document. The per-module specs (in `docs/superpowers/specs/`) reference this for the
> big picture: what Themia *is*, where each capability comes from, and the build order.
> Status date: 2026-06-01.

## What Themia is

**Themia** is the go-forward .NET 8/10 application framework (a rebrand of the in-repo
`zenity`/`zenity-v2`). It is a **framework core** + a catalog of **pluggable modules**
(`IThemiaModule`, ADR-0003), assembled from four existing codebases. `Idevs.Foundation` is
being dropped; `Zenity` becomes `Themia` (rename = separate task).

Brand: `Themia.*` (modern coinage from *Themis*; NuGet-clean & reservable). Neutral,
framework-agnostic libraries also live under `Themia.*` but carry **no** `Themia.Framework`
dependency, so non-Themia apps (e.g. Serenity/PowerACC) can consume them too.

## Source codebases (what each contributes)

| Source | Role | Contributes |
|---|---|---|
| **Zenity** (`zenity`, `zenity-v2`) | becomes the Themia **framework core** | DDD core, EF Core + tenant data, ASP.NET integration, MultiTenancy, Mediator + reflection-free SourceGenerator, Caching (Redis/Garnet/Valkey), Logging, Services, module system |
| **ezy-assets** (clean-arch SaaS, WIP) | pattern/code donor | ProblemDetails + typed exceptions, JWT/auth/tenant context, S3/Local storage, Email/OTP/SMS, PDF, Geo, AI, mediator pipeline behaviors, FluentMigrator runner |
| **PowerACC** (Serenity app) | pattern/code donor | SilkierQuartz dashboard, execution history, StackExchange.Exceptional store, CloudUploadStorage, ClamAV scan, reporting |
| **Idevs.Net.CoreLib** (Serenity lib) | splits in two | **Serenity-free infra → Themia**; **Serenity-only → stays** as the Serenity adapter family |

## The framework / app boundary (scope guard)

**Only framework + cross-cutting infra enters Themia.** Business domains stay in their apps:
ezy-assets' `Billing/CRM/Inventory/Sales/Tagging/Search/Dashboard` and PowerACC's accounting
domain are **not** Themia. Themia gives them the framework + modules to build on.

## Layered architecture

```
Tooling           Themia.SourceGenerator (DI + mediator, reflection-free) | Themia.Analyzers
(build-time)      Themia.Analyzers.CodeFixes | Themia.Generators.Abstractions  — see §E
─────────────────────────────────────────────────────────────────────────────────────────────
Framework core    Themia.Framework.Core | .Data.EFCore | .AspNetCore | Themia.MultiTenancy
(from Zenity)      Themia.Mediator | Themia.Caching | Themia.Logging | Themia.Services
                  Module system: IThemiaModule / ModuleDescriptor (ADR-0003)
─────────────────────────────────────────────────────────────────────────────────────────────
Neutral cores     Themia.Quartz | Themia.Exceptional(.SqlServer/.MySql/.PostgreSql)
(no Framework dep) → consumable by BOTH Themia apps and Serenity (PowerACC)
─────────────────────────────────────────────────────────────────────────────────────────────
Modules           Themia.Modules.* (Scheduling, ExceptionLogging, Identity, Storage,
(IThemiaModule)   Notifications, Pdf, Export, Geo, AI, Audit) — depend on Framework + neutral cores
─────────────────────────────────────────────────────────────────────────────────────────────
[DEFERRED]        Idevs.Net.CoreLib.* (Quartz, Exceptional.*) — Serenity adapter, neutral core + Serenity
                  → built ONLY if/when PowerACC migrates (optional reuse, not a driver)
```

Cross-cutting concerns follow a **three-layer pattern**: neutral core (`Themia.X`) →
Themia module (`Themia.Modules.X`) → **[deferred]** Serenity adapter (`Idevs.Net.CoreLib.X`).
**PowerACC is not a design driver** — it is an optional future consumer; the Serenity adapter is
built only when it actually adopts. The neutral cores are kept Serenity-free *to preserve that
option* (cheap), not because PowerACC needs them now.

## Target frameworks (layered policy)

- **Neutral cores + cross-framework `Themia.*`** (`Themia.Quartz`, `Themia.Exceptional(.provider)`,
  `Themia.AspNetCore` = typed exceptions + ProblemDetails, **standalone, no framework dep**) →
  **`net8.0;net10.0`**. The net8 leg is **mandatory** — PowerACC
  (net8) cannot reference net10-only packages, and net10-only would kill the reuse option above.
- **`Themia.Framework.*` (core/data) + `Themia.Modules.*`** → **`net10.0`** (new apps; PowerACC
  doesn't touch this layer). May widen to net8 if LTS breadth is wanted.
- **Tooling** (`Themia.Analyzers`/`SourceGenerator`) → **`netstandard2.0`** (Roslyn; build-time, TFM-agnostic).
- **.NET 12** added when released — cannot multi-target an unreleased TFM now. net8 + net10 are
  both LTS; that pair is the baseline.

## Repository layout

One repo (`Packages/themia/`) holds every Themia package, **grouped by layer** under `src/`
(mirroring zenity-v2's `src/framework`+`src/modules`). Serenity adapters stay in the separate
`Idevs.Net.CoreLib` repo (deferred). Single `Themia.sln`, one `Directory.Build.props` /
`Directory.Packages.props`, shared version — all packages release together.

```
Packages/themia/
├── Themia.sln · Directory.Build.props · Directory.Packages.props · LICENSE · README · CLAUDE.md
├── .github/{workflows/{ci,integration,release}.yml, release.yml, dependabot.yml}
├── docs/{themia-architecture-overview.md, superpowers/{specs,plans}/}
├── src/
│   ├── tooling/    # netstandard2.0 — Themia.Generators.Abstractions, .SourceGenerator, .Analyzers(.CodeFixes)
│   ├── neutral/    # net8.0;net10.0 — Themia.AspNetCore, Themia.Quartz, Themia.Exceptional(.SqlServer/.MySql/.PostgreSql)
│   ├── framework/  # net10.0 — Themia.Framework.{Core,Data.EFCore,AspNetCore}, .MultiTenancy, .Mediator, .Caching, .Logging, .Services  (moved in from zenity-v2 at rename)
│   └── modules/    # net10.0 — Themia.Modules.{Scheduling,ExceptionLogging,Identity,Storage,…}
├── tests/          # flat: <Package>.Tests/  (Exceptional.Tests carries [Trait("Category","Integration")])
└── samples/        # optional example apps
```

Each package folder = `<Name>.csproj` + `PublicAPI.{Shipped,Unshipped}.txt` + sources.
**TFM is set per-csproj** (Directory.Build.props does not set it) — the `src/<layer>/` folder tells
you the target: `neutral/` = net8.0;net10.0, everything else = net10.0 (tooling = netstandard2.0).

## A. Framework core (rename from Zenity)

| Themia | from Zenity | capability |
|---|---|---|
| `Themia.Framework.Core` | Framework.Core | Entity/ValueObject/Result, Domain Events, multi-tenant |
| `Themia.Framework.Data.EFCore` | Framework.Data.EFCore | EF Core + tenant isolation + audit + provider abstraction (**canonical data layer**) |
| `Themia.Framework.AspNetCore` | Framework.AspNetCore | ASP.NET integration |
| `Themia.MultiTenancy` | MultiTenancy | tenant resolution/DI |
| `Themia.Mediator` + `Themia.SourceGenerator` | Mediator/SourceGenerator | CQRS dispatch, compile-time, reflection-free |
| `Themia.Caching` | Core.Caching | Memory/Redis/Garnet/Valkey + MessagePack |
| `Themia.Logging` | Core.Logging | Serilog-based |
| `Themia.Services` | Services | Domain/Infra/Integration service abstractions |

## B. Module catalog (cross-cutting) — convergence of sources

| Themia module | sources (best-of merge) | status |
|---|---|---|
| `Themia.Modules.Scheduling` (+ `Themia.Quartz`) | PowerACC SilkierQuartz | **✅ built** (`Themia.Quartz` neutral core + `Themia.Modules.Scheduling`; dashboard smoke + EF store integration green) |
| `Themia.Modules.ExceptionLogging` (+ `Themia.Exceptional.*`) | PowerACC/Idevs custom Dapper dialect engine (3 DB). *typed-exceptions + ProblemDetails split out to standalone neutral `Themia.AspNetCore`* | **✅ specced** |
| `Themia.Modules.Identity` | ezy-assets `Jwt/Authentication/RoleAccess/TenantContext/LineLogin` + claims/policies + Zenity Identity.Example | ⬜ **P1 — next to spec** |
| `Themia.Modules.Storage` | **ezy-assets** S3/Local + **Idevs** `CloudUploadStorage` + **PowerACC** ClamAV scan | ⬜ **P1 — next to spec** |
| `Themia.Modules.Notifications` | ezy-assets `NotificationDispatcher`/Email/OTP/`Sms2Pro` | ⬜ to-spec |
| `Themia.Modules.Pdf` | **ezy-assets** Contract/Proposal PDF + **Idevs** `PdfOptionsBuilder`/PuppeteerSharp + PowerACC reporting | ⬜ to-spec |
| `Themia.Modules.Export` | **Idevs** `IReportBaseModel`/`IdevsExportRequest`/ClosedXML (Excel) | ⬜ to-spec |
| `Themia.Modules.Geo` | ezy-assets `ProjectGeocodingService` | ⬜ later |
| `Themia.Modules.AI` | ezy-assets `GeminiAICaption`/`FallbackTextTranslation` | ⬜ later |
| `Themia.Modules.Audit` | ezy-assets `AuditLogRepository` + Zenity audit | ⬜ later |

## C. Mediator pipeline behaviors → `Themia.Mediator`

From ezy-assets: `LoggingBehavior`, `TenantBehavior`, `ValidationBehavior` + `ISkipTenantValidation`.
Lift into Themia.Mediator's pipeline (Zenity already provides mediator + pipeline + source-gen).

## D. Idevs.Net.CoreLib disposition (split)

**Serenity-FREE → migrate into Themia** (strangler; de-Serenity-ize as they move):

| Idevs file/area | → Themia |
|---|---|
| `Storage/CloudUploadStorage(+Options)` | `Themia.Modules.Storage` |
| `Helpers/PdfOptionsBuilder` + `Models/PageSize`/`PageMargin` (PuppeteerSharp) | `Themia.Modules.Pdf` |
| `Models/IReportBaseModel`/`IdevsExportRequest`/`IdevsContentResult` (ClosedXML) | `Themia.Modules.Export` |
| `Caching/TwoLevelCacheExtensions` | `Themia.Caching` |
| `Logging/LogManager` | `Themia.Logging` |
| `Utilities/SmartPagination`, `Extensions/*`, `CoreLibBootstrapper` | `Themia` utilities |

**Serenity-ONLY → stays in `Idevs.Net.CoreLib`** (it becomes the Serenity adapter family):
- `Repositories/*` — `SqlServiceBase`, `RowRepositoryBase`, `UnitOfWorkScope`, `RowLock*`,
  **`Sequences/*`** (document-numbering: `ISequenceProvider`/`SqlSequenceProvider`),
  `RowVersion*`, `OptimisticConcurrencyException`, `ConnectionKeyAttribute` (Serenity `ISqlConnections`).
- `ComponentModels/*` — Serenity UI editor/formatter attributes (100% Serenity).
- The Serenity adapters: `Idevs.Net.CoreLib.Quartz`, `Idevs.Net.CoreLib.Exceptional.*`.

**Sequences (document numbering)** — **DECIDED (#2): port into `Themia.Framework.Data`.** See §F.
**Tooling** — **DECIDED (#3): move to Themia.** Idevs `.Generators`/`.CodeFixes` merge with
Zenity's mediator source-gen into one Themia tooling family. See §E.

## E. Tooling family (DECISION #3 — move to Themia)

Build-time, reflection-free, framework-neutral. Merges Idevs `.Generators`/`.CodeFixes` with
Zenity's mediator source-gen. Referenced by **both** Themia apps and PowerACC (Serenity) directly
— no runtime Serenity coupling.

| Package | from | role |
|---|---|---|
| `Themia.SourceGenerator` | Idevs DI-gen + Zenity mediator-gen | reflection-free: `[Scoped/Singleton/Transient]` DI registration **+** mediator handler registration/dispatch |
| `Themia.Analyzers` | Idevs `IDEVSGEN1xx` | misuse rules: 2+ connections w/o UoW, log-and-rethrow, sync-over-async Task body, hand-rolled `MAX()+1` sequence |
| `Themia.Analyzers.CodeFixes` | Idevs CodeFixes | auto-fixes (e.g. scaffold `ISequenceProvider.NextAsync`) |
| `Themia.Generators.Abstractions` | Idevs Abstractions | Lifetime/Scanner/Writer/Diagnostics + DI marker attributes |

Port tasks: rename diagnostic IDs `IDEVSGEN1xx → THEMIA1xx`; unify the DI + mediator attribute
model into Themia abstractions; **re-target** the UoW + sequence analyzers to Themia types
(depends on §F). Synergy with §F: rule (hand-rolled sequence) + its codefix steer devs onto
`ISequenceProvider` — Themia ships the full "document numbering done right" set (provider +
analyzer + autofix).

## F. Sequences / document numbering (DECISION #2 — port to Themia.Framework.Data)

Idevs' `ISequenceProvider` is proven (atomic alloc in a **separate** transaction → survives outer
rollback; gaps OK, dups catastrophic; `SELECT…FOR UPDATE`; overflow-checked; multi-DB UPSERT for
SqlServer/MySQL/Postgres already written). Only the **storage** is Serenity-coupled.

Port: move the neutral `ISequenceProvider` + semantics into `Themia.Framework.Data`; add
`EfSequenceProvider` using `ExecuteSqlRaw/FromSqlRaw` with the existing per-dialect lock/upsert
SQL; **keep the separate-transaction semantic**; **add tenant scoping** (`(TenantId, SequenceKey)`
PK — the current one lacks it); ship an EF migration for the `Sequences` table (3 DB). Native
`CREATE SEQUENCE` is rejected (MySQL 8 has none) → table-based allocator is the portable choice.
Serenity adapter: PowerACC's `SqlSequenceProvider` stays, implementing the same interface.
Optional value-add: a separate `IDocumentNumberFormatter` (prefix/year/padding/reset via key
convention) — kept apart from the allocator.

## Data layer (DECISION #1 — EF-default + sanctioned read-only Dapper hatch)

`Themia.Framework.Data.EFCore` (EF Core) is the **canonical, default** data layer — it enforces
tenant isolation (global query filters) + audit + UoW centrally and abstracts the 3-DB SQL.
**Dapper is allowed only as a controlled read-only escape-hatch** through a framework-sanctioned
query API that (1) shares EF's connection/transaction, (2) auto-injects / forces the tenant
predicate, (3) is read-only by convention — fits the existing CQRS split (EF commands, Dapper
read-models). **Start Phase 1 EF-only; open the hatch only with profiling data.** Rationale: raw
Dapper risks **tenant-isolation bypass** (a critical leak) and **multiplies the 3-DB SQL burden**
that EF hides. ezy-assets (Dapper) / Idevs (`SqlServiceBase`) implementations are not lifted
wholesale — only their *patterns* (UoW, optimistic concurrency, row-lock, sequences) inform the
abstractions.

## Multi-database requirement

Phase 1 relational support across the framework + data-backed modules: **SQL Server, MySQL
(incl. MariaDB), PostgreSQL** (via dialect strategy + per-provider packages, per the Exception
spec). Later phases extend (SQLite, Oracle) without public-surface breaks.

## Phase roadmap (proposed)

> **Phase ≠ version.** Phases are a *build-priority* grouping; the published *version* is a separate
> single-shared counter. They do not align 1:1. Build order, release cadence, and the version each
> deliverable ships at are defined in
> [`release-strategy spec`](superpowers/specs/2026-06-01-themia-release-strategy-design.md)
> (chosen order: `0.1.0` AspNetCore → `0.2.0` framework rename → `0.3.0` remaining neutral cores →
> `0.4.0` Phase-1 modules → … → `1.0.0`).

- **Phase 0 — Rename** `zenity`/`zenity-v2` → `Themia.Framework.*`/`Themia.Modules.*` (separate task).
- **Phase 1 — Core cross-cutting:** Scheduling ✅, ExceptionLogging ✅, **Identity**, **Storage**
  (+ multi-DB SqlServer/MySql/Postgres baseline).
- **Phase 2 — Productivity:** Notifications, Pdf, Export.
- **Phase 3 — Advanced:** Geo, AI, Audit; Sequences EF-port; SourceGenerator/analyzer merge.
- **Ongoing — Strangler:** migrate Idevs.Net.CoreLib's Serenity-free infra into Themia per module.

## Specs index

- ✅ `docs/superpowers/specs/2026-06-01-themia-release-strategy-design.md` (versioning + build order)
- ✅ `docs/superpowers/specs/2026-06-01-themia-quartz-scheduling-design.md`
- ✅ `docs/superpowers/specs/2026-06-01-themia-exceptional-design.md`
- ⬜ Phase 0 framework rename (`0.2.0`) — own spec when started
- ⬜ Identity, Storage, Notifications, Pdf, Export, … (one spec each, this catalog as parent)

## Decisions

**Resolved (2026-06-01):**
1. ✅ **Data layer** — EF-default (canonical) + sanctioned **read-only** Dapper escape-hatch
   (shares EF tx, tenant-safe); Phase 1 EF-only, open the hatch only on profiling. See Data layer §.
2. ✅ **Sequences** — port `ISequenceProvider` into `Themia.Framework.Data` (tenant-aware,
   table-based, 3-DB, separate-tx semantic) + optional formatter. See §F.
3. ✅ **Tooling** — move to Themia as a build-time family (`Themia.SourceGenerator` +
   `Themia.Analyzers` + `.CodeFixes` + `.Generators.Abstractions`), merging Zenity mediator-gen.
   See §E.
4. ✅ **Module naming** — capability-named (`Scheduling`, `ExceptionLogging`), applied consistently.
5. ✅ **Phase-1 module set** — **Scheduling, ExceptionLogging, Identity, Storage** (the two
   specced + Identity + Storage). Identity + Storage next to be specced.

_All open decisions resolved._
