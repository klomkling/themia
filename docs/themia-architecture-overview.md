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
(build-time)      Themia.Generators.Abstractions  — see §E (no .CodeFixes: deferred, never built)
─────────────────────────────────────────────────────────────────────────────────────────────
Framework core    Themia.Framework (metapackage) | .Core | .Data.Abstractions | .AspNetCore
(from Zenity)     .Data.EFCore(.PostgreSql/.SqlServer) | .Data.Dapper(.PostgreSql/.MySql/.SqlServer)
                  .Data.Sequences | Themia.MultiTenancy(.Mediator) | Themia.Mediator
                  Themia.Caching | Themia.Logging | Themia.Services
                  Module system: IThemiaModule / ModuleDescriptor (ADR-0003)
─────────────────────────────────────────────────────────────────────────────────────────────
Neutral cores     Themia.AspNetCore(.DataProtection.{SqlServer/MySql/PostgreSql}) | Themia.Quartz | Themia.Scheduling
(no Framework dep) Themia.Exceptional(.SqlServer/.MySql/.PostgreSql/.AspNetCore) | Themia.Audit(+3 engines/.AspNetCore)
                  Themia.Messaging(+3 engines/.Hmac/.Http/.AspNetCore) | Themia.Notifications | Themia.Pdf
                  Themia.Storage(.S3) | Themia.Export(.Excel) | Themia.Geo(.Google) | Themia.AI(.Gemini/.OpenAiCompatible)
                  Themia.Challenges(+3 engines) | Themia.Totp | Themia.WebAuthn | Themia.PromptPay | Themia.Imaging
                  Themia.Data.Migrations(+3 engines) | Themia.Data.Probes | Themia.DependencyInjection
                  → consumable by BOTH Themia apps and Serenity (PowerACC)
─────────────────────────────────────────────────────────────────────────────────────────────
Modules           Themia.Modules.* (Scheduling, Identity(+.Abstractions/.EFCore/.Dapper/.AspNetCore/
(IThemiaModule)    .ExternalAuth.AspNetCore/.Tokens.AspNetCore), Storage, Notifications(+3 engines),
                  Pdf, Export, Audit, Messaging) — depend on Framework + neutral cores
                  (no Themia.Modules.Geo, .AI or .ExceptionLogging — none has tenant state or a
                   schema of its own; see §B)
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
│   ├── tooling/    # netstandard2.0 — Themia.Generators.Abstractions, .SourceGenerator, .Analyzers  (no .CodeFixes — see §E)
│   ├── neutral/    # net8.0;net10.0 — Themia.AspNetCore, .Quartz, .Exceptional(.provider), .Audit, .Messaging, .Storage, … (see §"Layered architecture")
│   ├── framework/  # net10.0 — Themia.Framework.{Core,Data.EFCore,AspNetCore}, .MultiTenancy, .Mediator, .Caching, .Logging, .Services  (moved in from zenity-v2 at rename)
│   └── modules/    # net10.0 — Themia.Modules.{Scheduling,Identity,Storage,Notifications,Pdf,Export,Audit,Messaging}
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
| `Themia.Framework.Data.Abstractions` | — (new) | the shared data contract both peers implement (UoW, tenant predicate, soft-delete) |
| `Themia.Framework.Data.EFCore` (+ `.PostgreSql`, `.SqlServer`) | Framework.Data.EFCore | EF Core + tenant isolation + audit + provider abstraction (**a first-class data-access peer** — see DECISION #6) |
| `Themia.Framework.Data.Dapper` (+ `.PostgreSql`, `.MySql`, `.SqlServer`) | — (new, 0.4.1–0.4.4) | Dapper + SqlKata, **the co-equal peer** to EF Core over the same schema (DECISION #6) |
| `Themia.Framework.Data.Sequences` | Idevs `ISequenceProvider` | atomic tenant-scoped document numbering (0.22.0 — realises DECISION #2; see §F) |
| `Themia.Framework.AspNetCore` | Framework.AspNetCore | ASP.NET integration |
| `Themia.Framework` | — (new, 0.8.0) | **metapackage**: one reference for the core set. Deliberately excludes the data peer — the adopter adds exactly one `.Data.EFCore.*` or `.Data.Dapper.*` to complete the stack |
| `Themia.MultiTenancy` (+ `.Mediator`) | MultiTenancy | tenant resolution/DI; typed `TenantId` + claims resolution 0.5.6; tenant-presence guard 0.5.7 |
| `Themia.Mediator` + `Themia.SourceGenerator` | Mediator/SourceGenerator | CQRS dispatch, compile-time, reflection-free |
| `Themia.Caching` | Core.Caching | Memory/Redis/Garnet/Valkey + MessagePack |
| `Themia.Logging` | Core.Logging | Serilog-based |
| `Themia.Services` | Services | Domain/Infra/Integration service abstractions |

## B. Module catalog (cross-cutting) — convergence of sources

| Themia module | sources (best-of merge) | status |
|---|---|---|
| `Themia.Modules.Scheduling` (+ `Themia.Quartz`, `Themia.Scheduling`) | PowerACC SilkierQuartz | ✅ **built** (`Themia.Quartz` neutral core + `Themia.Modules.Scheduling`; dashboard smoke + EF store integration green. Scheduling schema moved EF→FluentMigrator in 0.4.7; persistent Quartz/AdoJobStore in 0.4.8; the persistent scheduler split out of the EF-bound module into the neutral `Themia.Scheduling` in 0.15.0) |
| ~~`Themia.Modules.ExceptionLogging`~~ — no module; shipped as the neutral `Themia.Exceptional` family | PowerACC/Idevs custom Dapper dialect engine (3 DB). *typed-exceptions + ProblemDetails split out to standalone neutral `Themia.AspNetCore`* | ✅ **built** (0.3.0 — `Themia.Exceptional` + PostgreSQL dialect, `.SqlServer`/`.MySql` alongside; `Themia.Exceptional.AspNetCore` dashboard 0.5.8, StackExchange.Exceptional parity + request-context capture 0.6.1. **No `Themia.Modules.ExceptionLogging` package exists** — capture/persist/dashboard carry no tenant-scoped schema of their own, so the module layer was never built; same per-capability check as the Geo/AI correction below) |
| `Themia.Modules.Identity` (+ `.Abstractions`) | ezy-assets `Jwt/Authentication/RoleAccess/TenantContext/LineLogin` + claims/policies + Zenity Identity.Example | **✅ built** (0.5.0 — tenant-aware user/role/claim store, argon2id, `ICurrentUser`, EF+Dapper, PostgreSQL+SQL Server FM schema) |
| `Themia.Modules.Identity.AspNetCore` | ezy-assets JWT + authentication flows | **✅ built** (0.5.2 — external/OAuth login: pluggable providers + Google/LINE, `AddThemiaExternalAuth`, `MapIdentityExternalAuthEndpoints`; Facebook/Microsoft/Telegram deferred additive providers — on top of 0.5.1 JWT issuance, rotating refresh tokens, `IAuthenticationFlow`, `MapIdentityAuthEndpoints`) |
| `Themia.Modules.Storage` (+ `Themia.Storage`, `.S3`) | **ezy-assets** S3/Local + **Idevs** `CloudUploadStorage` + **PowerACC** ClamAV scan | ✅ **built** (0.5.3 — Local + S3/R2 backends, tenant-aware metadata + quota, EF+Dapper, PostgreSQL+SQL Server FM schema; Local presigned-transfer routes hardened 0.8.8; permanent unsigned **absolute** public URLs + `X-Content-Type-Options: nosniff` on the serving route 0.9.0, coord #0022) |
| `Themia.Modules.Notifications` (+ `Themia.Notifications`, `.PostgreSql/.MySql/.SqlServer`) | ezy-assets `NotificationDispatcher`/Email/OTP/`Sms2Pro` | ✅ **built** (0.6.2 neutral `Themia.Notifications` sending core → 0.6.3 tenant-aware module + outbox/drainer/dispatcher + the three per-engine packages; MySQL outbox-claim deadlock under concurrent drainers fixed 0.6.4. **(breaking)** the drain loop moved into `Themia.Messaging` in 0.11.0 so both modules share one implementation instead of forking it; `NotConfigured` sender result mapped through the outbox in 0.12.0) |
| `Themia.Modules.Pdf` (+ `Themia.Pdf`) | **ezy-assets** Contract/Proposal PDF + **Idevs** `PdfOptionsBuilder`/PuppeteerSharp + PowerACC reporting | ✅ **built** (0.6.0 neutral `Themia.Pdf` HTML→PDF core, Handlebars templates → 0.7.0 tenant-aware template store with global-default fallback + render-by-key; `ThemiaPdfOptions.MaxConcurrency` bounds concurrent renders 0.10.1, coord #0046 — they were completely ungated before) |
| `Themia.Export` + `Themia.Export.Excel` | **Idevs** `IReportBaseModel`/`IdevsExportRequest`/ClosedXML (Excel), de-Serenity-ized | ✅ **built** (0.6.8 — two stateless neutral cores: typed columns, CSV + xlsx, computed summary rows; no tenant module — the transform is stateless) |
| `Themia.Modules.Export` | — (new; no prior source) | ✅ **built** (0.6.9 — tenant-aware async export module: `IExportDefinition<TParams>` keyed definitions; on-demand + cron Quartz jobs; Storage delivery via signed link; completion/failure Notifications; 7-day retention cleanup; opt-in `BypassSoftDeleteFilter` for full-data exports; FM schema, PostgreSQL+SQL Server+MySQL) |
| ~~`Themia.Modules.Geo`~~ — no module; see `Themia.Geo` + `Themia.Geo.Google` in §"Neutral cores" below | ezy-assets `ProjectGeocodingService` | ✅ **built** (0.24.0 — no tenant state, no schema, so there is no module; see correction below) |
| ~~`Themia.Modules.AI`~~ — no module; see `Themia.AI` + `Themia.AI.Gemini` + `Themia.AI.OpenAiCompatible` below | ezy-assets `GeminiAICaption`/`FallbackTextTranslation` | ✅ **built** (0.24.0 — no tenant state, no schema, so there is no module; see correction below) |
| `Themia.Modules.Audit` (+ `Themia.Audit`, `.PostgreSql/.SqlServer/.MySql`, `.AspNetCore`) | **new** — the listed sources turned out to describe something else (see below) | ✅ **built** (0.23.0 — append-only activity + authentication event log; unqualified `themia_audit_events` on all three engines; unconditional redaction; `RequireTransaction` for activity events on EF and Dapper alike; `IIdentityEventObserver` audits all twelve Identity events; fail-closed read-only dashboard. Entity change log deferred to 0.24.0) |
| `Themia.Modules.Messaging` (+ `Themia.Messaging`, `.PostgreSql/.MySql/.SqlServer`, `.Hmac`, `.Http`, `.AspNetCore`) | **new** — service-to-service messaging for the two consumer apps (coord #0050) | ✅ **built** (0.11.0 — neutral transactional outbox/inbox across the three engines, the `themia-hmac-v1` signing scheme shared by both ends of a channel, an HTTP dispatcher that signs and delivers a claimed row, and a receiving minimal-API endpoint filter; tenant-aware module on top. A service's identity was configured twice and was unified into one `MessagingIdentity` in the same line of work) |

> **The `Themia.Modules.Audit` sources listed here were wrong, and the correction is worth keeping.**
> ezy-assets' `AuditLogRepository` has `OldValue`/`NewValue` `jsonb` columns, so it reads like an entity
> change log. All 46 of its call sites are hand-written, `Action` is a business verb
> (`PROPOSAL_INTEREST_SUBMITTED`), and `OldValue` holds a hand-picked field subset, never a computed row
> diff — it is an **activity log with two columns named old/new**. `AuditEvent` in `Themia.Services` has
> no old/new fields at all. The entity change log most people mean by "audit" existed in neither source
> and is being built from scratch in `0.24.0`.

> **The `Themia.Modules.Geo` and `Themia.Modules.AI` rows were wrong on two points each, and both
> corrections are worth keeping.**
>
> **There is no module, for either.** Neither `Themia.Geo` nor `Themia.AI` has tenant-scoped state, a
> schema, or an `IThemiaModule` lifecycle to run — `Themia.Geo` is coordinate primitives plus an HTTP
> geocoding call, and `Themia.AI` is an HTTP completion dispatcher plus one typed operation built on it.
> A module wrapper would add a package whose only content is DI registration the neutral package's own
> `AddThemiaGeoGoogle`/`AddThemiaAiGemini`/`AddThemiaAiOpenAiCompatible`/`AddThemiaAi` already does.
> Contrast `Themia.Modules.Audit` directly above, which genuinely needed a module for tenant resolution
> and transaction enlistment — the pattern was checked per capability, not copied automatically.
>
> **The "sources" column named the wrong thing for both — read as ported code, both are placeholders
> or plumbing, not the capability being built.** `ProjectGeocodingService` (the `Themia.Geo` source) is
> mostly ezy-assets domain SQL — the code worth reading in it is a hand-rolled Haversine calculation and
> an HTTP call to Google's Geocoding API; there is no POI store, gazetteer, or name-matching logic in
> it, and `Themia.Geo` deliberately does not add any of those either (both consumer apps asked it not
> to — coord #0115). `GeminiAICaption`/`FallbackTextTranslation` (the `Themia.AI` sources) **contain no
> AI call at all**: the caption service formats request fields into a `StringBuilder` with emoji (its
> own comment calls it a placeholder that "simulates AI"), and the translation service returns its input
> unchanged. `Themia.AI` is therefore not a port of either — it is the first real implementation, built
> against the provider's actual REST shape instead of against ported code that never called one.
>
> See `docs/superpowers/specs/2026-09-08-themia-geo-design.md` and
> `docs/superpowers/specs/2026-09-08-themia-ai-design.md` for the full design and this correction's
> detail.

## C. Mediator pipeline behaviors → `Themia.Mediator`

From ezy-assets: `LoggingBehavior`, `TenantBehavior`, `ValidationBehavior` + `ISkipTenantValidation`.
Lift into Themia.Mediator's pipeline (Zenity already provides mediator + pipeline + source-gen).

## D. Idevs.Net.CoreLib disposition (split)

**Serenity-FREE → migrate into Themia** (strangler; de-Serenity-ize as they move):

| Idevs file/area | → Themia |
|---|---|
| `Storage/CloudUploadStorage(+Options)` | `Themia.Modules.Storage` |
| `Helpers/PdfOptionsBuilder` + `Models/PageSize`/`PageMargin` (PuppeteerSharp) | `Themia.Modules.Pdf` |
| `Models/IReportBaseModel`/`IdevsExportRequest`/`IdevsContentResult` (ClosedXML) | `Themia.Export` / `Themia.Export.Excel` |
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
| ~~`Themia.Analyzers.CodeFixes`~~ | Idevs CodeFixes | ⬜ **not built** — auto-fixes (e.g. scaffold `ISequenceProvider.NextAsync`) were planned but no such project exists and no `CodeFixProvider` ships anywhere in `src/tooling/`. The analyzers below are diagnostic-only |
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

> ⚠️ **SUPERSEDED 2026-06-11** by **DECISION #6 — Data-access peers & schema authority** (below).
> Dapper is now a write-capable first-class peer, not a read-only hatch. Retained here for rationale/history.

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

## Data-access peers & schema authority (DECISION #6 — 2026-06-11, supersedes #1)

The 0.4.x work gave Dapper a full write path (`DapperUnitOfWork`, store-generated keys) and three
engines (PostgreSQL · MySQL · SQL Server), so the original "EF-default, Dapper = read-only hatch"
framing no longer matches the code. Resolved direction:

1. **EF Core and Dapper are selectable first-class peers.** An adopter chooses one; the whole
   framework runs on that choice. Modules stay access-agnostic — they code to
   `Themia.Framework.Data.Abstractions` (`IRepository`/`IReadRepository`/`IUnitOfWork`); the host
   registers either `Themia.Framework.Data.EFCore` or `Themia.Framework.Data.Dapper(.<engine>)`.
   **One implementation app-wide** — not per-module EF/Dapper variants.
2. **FluentMigrator is the single schema/DDL authority for all framework-owned tables**, across
   both access layers and all engines (one migration with `IfDatabase(...)` branches — as
   `Themia.Exceptional` already does). **No module uses `dotnet ef migrations add`.** Consequence:
   `Themia.Modules.Scheduling`'s EF-generated, Postgres-typed `InitialScheduling` migration is
   rewritten as FluentMigrator (and reconciled with Quartz.NET's own `qrtz_*` schema); a single
   aggregating FluentMigrator runner collects every module's migrations and runs them per provider.
3. **The gate on calling Dapper "first-class" is tenant-isolation parity with EF.** Through the
   repositories/UoW, Dapper already matches EF (reads seed `WHERE tenant_id …`; writes put the tenant
   predicate *inside* the UPDATE/DELETE and throw on 0 rows — tighter than EF's read-then-write). The
   gap is structural: EF enforces isolation **by construction** (model-level query filters, default-safe),
   Dapper enforces it **by convention** (only when access flows through the repo). The raw connection
   (`IDapperConnectionContext.GetOpenConnectionAsync`) is an ambient, unguarded bypass. Acceptance
   criteria to close it: (a) `ITenantQueryFactory.For<T>()` — already tenant-seeded — is the blessed
   path for ad-hoc queries; (b) the raw connection becomes a conspicuous, reviewable escape hatch
   (explicit bypass scope / segregated API); (c) a **`Themia.Analyzers` build-time rule** flags
   raw-connection use outside the data-access assembly, making the safe path inescapable without
   runtime reflection (per the project's `dotnet.md` "avoid reflection; prefer analyzers" rule).
   The same analyzer work must also cover the **EF side's residual hole**: `DbSet<T>.Find/FindAsync`
   bypasses `ThemiaDbContext`'s tenant post-check for already-tracked entities (EF identity-map
   semantics; the guarded path is `DbContext.FindAsync<T>` / `EfReadRepository.GetByIdAsync` — see
   `docs/2026-06-11-efcore-sqlserver-find-isolation-issue.md`). Rule: flag direct `DbSet.Find*` and
   `Set<T>().Find*` calls outside the data layer, steering callers to the guarded APIs.

**Export filter scope — `BypassSoftDeleteFilter` (added 0.6.9).** `IDataFilterScope` (on both
EF Core and Dapper implementations) exposes `BypassSoftDeleteFilter()` — a scoped opt-in that
suppresses the framework's `IsDeleted = false` global query filter for the duration of a single
export run. Activated only when `IExportDefinition.AllowsIncludeSoftDeleted` is `true` and the
caller sets the flag in `ExportContext`. Use outside export jobs requires explicit reviewer
sign-off; a `Themia.Analyzers` rule flags ambient bypass outside the designated export scope.

**Per-provider concurrency token** is the cross-cutting follow-up (the `ApplyConcurrencyTokens`
landmine in `ThemiaDbContext`): Postgres `xmin` (no DDL), SQL Server `rowversion`, MySQL an
app-updated token — FluentMigrator provisions the right column per engine, and the Dapper layer
needs a matching concurrency story (it has none yet). _Deferred: no framework table uses
`IConcurrencyAware` yet, so the per-engine concurrency DDL helper is built only when a consuming
module first needs it; the EF concurrency-seam refactor rides with the EF MySQL provider._

## Multi-database requirement

Phase 1 relational support across the framework + data-backed modules: **SQL Server, MySQL 8.0.13+,
PostgreSQL** (via dialect strategy + per-provider packages, per the Exception spec). Later phases
extend (SQLite, Oracle) without public-surface breaks.

**MariaDB is not supported** (UPDATED 2026-08-04 — supersedes the earlier "MySQL (incl. MariaDB)"
claim). The MySQL leg of the shared schema uses **functional key parts** (`CREATE UNIQUE INDEX ... ((expr))`,
MySQL 8.0.13+) to emulate the partial/filtered unique indexes PostgreSQL and SQL Server have natively —
see `Themia.Modules.Pdf.Migrations.PdfTemplateSchemaMigration` and
`Themia.Challenges.Migrations.ChallengeSchemaMigration`. MariaDB has no equivalent syntax at any
version, so those migrations fail to parse and the module cannot install. The claim was inherited
across specs and never tested; only `Themia.Data.Migrations` ever had a real MariaDB container test
(`MigrationLockTests`, `mariadb:11` — it still runs, and its advisory-lock semantics are deliberately
written to hold on both engines). Supporting MariaDB would mean replacing every functional index with a
persisted generated column plus an index on it, across every module that uses one — deferred until an
adopter actually needs it.

## Phase roadmap (Phases 0–3 delivered)

> **Phase ≠ version.** Phases are a *build-priority* grouping; the published *version* is a separate
> single-shared counter. They do not align 1:1. Build order, release cadence, and the version each
> deliverable ships at are defined in
> [`release-strategy spec`](superpowers/specs/2026-06-01-themia-release-strategy-design.md)
> (chosen order: `0.1.0` AspNetCore → `0.2.0` framework rename → `0.3.0` remaining neutral cores →
> `0.4.0` Phase-1 modules → … → `1.0.0`).

- **Phase 0 — Rename** ✅ `zenity`/`zenity-v2` → `Themia.Framework.*`/`Themia.Modules.*` (0.2.0, tag
  `v0.2.0` — `docs/superpowers/specs/2026-06-02-themia-0.2.0-framework-rename-design.md`).
- **Phase 1 — Core cross-cutting:** Scheduling ✅, ExceptionLogging ✅ (neutral family only — no module, see §B), **Identity** ✅, Storage ✅
  (+ multi-DB SqlServer/MySql/Postgres baseline).
- **Phase 2 — Productivity:** **Notifications** ✅ (0.6.2/0.6.3), **Pdf** ✅ (0.6.0/0.7.0), **Export** ✅ (0.6.8/0.6.9).
- **Phase 3 — Advanced:** **Geo** ✅ (0.24.0 —
  `docs/superpowers/specs/2026-09-08-themia-geo-design.md`; no module, see the correction in §B) and
  **AI** ✅ (0.24.0 — `docs/superpowers/specs/2026-09-08-themia-ai-design.md`; no module, see the
  correction in §B); **Audit** ✅ (0.23.0 —
  `docs/superpowers/specs/2026-09-06-themia-audit-design.md`; entity change log deferred to 0.24.0);
  Sequences EF-port ✅ (shipped as
  `Themia.Framework.Data.Sequences`, see `docs/superpowers/specs/2026-09-05-themia-sequences-design.md`
  — §F below is superseded on three points, recorded in that spec);
  SourceGenerator/analyzer merge ✅ (one build-time family under `src/tooling/`, diagnostic IDs
  renamed `IDEVSGEN1xx` → `THEMIA1xx`; `Themia.Analyzers.CodeFixes` is the one piece never built — §E).
- **Deferred — Strangler:** migrating Idevs.Net.CoreLib's Serenity-free infra into Themia per module is
  **not in progress and never started** — `Idevs.Net.CoreLib` appears once in the whole changelog, and
  every reference to it here is marked deferred. The Serenity adapter family is built only if/when
  PowerACC actually migrates, and **PowerACC is not a design driver** (§D). The reusable infra it would
  have carried was instead written directly into Themia's neutral cores.

## Specs index

Every spec below has a sibling plan under `docs/superpowers/plans/` sharing its date-stem. Paths here
are relative to `docs/superpowers/specs/`. A version in parentheses is the release the spec shipped in.

**Foundation**

- ✅ `2026-06-01-themia-release-strategy-design.md` — versioning + build order (the roadmap this catalog serves)
- ✅ `2026-06-02-themia-0.2.0-framework-rename-design.md` — Phase 0 rename + cross-cutting consolidation (0.2.0, tag `v0.2.0`)
- ✅ `2026-07-11-themia-framework-metapackage-design.md` — `Themia.Framework` metapackage + package-selection docs (0.8.0)

**Data layer**

- ✅ `2026-06-07-themia-dapper-data-layer-design.md` — Dapper + SqlKata behind the shared abstraction (0.4.1, PostgreSQL)
- ✅ `2026-06-09-ef-write-path-tenant-enforcement-design.md` — EF + Dapper write-path tenant enforcement (0.4.2)
- ✅ `2026-06-10-themia-dapper-mysql-engine-design.md` (0.4.3) · `2026-06-10-themia-dapper-sqlserver-engine-design.md` (0.4.4)
- ✅ `2026-06-11-themia-efcore-sqlserver-provider-design.md` — EF SQL Server provider + per-engine package split (0.4.5)
- ✅ `2026-06-12-themia-data-migrations-runner-design.md` — shared FluentMigrator runner, `Themia.Data.Migrations` (0.4.6)
- ✅ `2026-09-09-data-migrations-engine-split.md` — engine runners split into `.PostgreSql`/`.MySql`/`.SqlServer` (0.25.0)
- ✅ `2026-08-23-schema-agreement-design.md` — migrations/store schema agreement → `Themia.Data.Probes` (0.17.0, coord #0088)
- ✅ `2026-09-05-themia-sequences-design.md` — document numbering (0.22.0; supersedes §F on three points, recorded in the spec)

**Tenancy & analyzers**

- ✅ `2026-06-13-themia-isolation-analyzer-gates-design.md` — tenant-isolation analyzer gates, THEMIA103/104 (0.4.9)
- ✅ `2026-06-18-themia-multitenancy-typed-tenantid-design.md` — typed `TenantId` + claims resolution (0.5.6, coord #0003)
- ✅ `2026-06-20-themia-tenant-guard-design.md` — tenant-presence guard, MultiTenancy + Mediator bridge (0.5.7)

**Scheduling**

- ✅ `2026-06-01-themia-quartz-scheduling-design.md` — Quartz dashboard (SilkierQuartz port)
- ✅ `2026-06-12-themia-scheduling-fluentmigrator-design.md` (0.4.7) · `2026-06-12-themia-persistent-quartz-design.md` — AdoJobStore (0.4.8)

**Exception logging**

- ✅ `2026-06-01-themia-exceptional-design.md` — the custom Dapper dialect engine, one schema across three engines
- ✅ `2026-06-05-themia-0.3.0-exceptional-neutral-core-design.md` — neutral core + PostgreSQL dialect (0.3.0)
- ✅ `2026-06-20-themia-exceptional-dashboard-design.md` (0.5.8, coord #0009) · `2026-06-22-themia-exceptional-dashboard-se-parity-design.md` — StackExchange parity + request-context capture (0.6.1)

**Identity**

- ✅ `2026-06-14-themia-identity-core-design.md` (0.5.0) · `2026-06-15-themia-identity-jwt-design.md` (0.5.1)
- ✅ `2026-06-16-themia-identity-external-login-design.md` — external/OAuth login (0.5.2)
- ✅ `2026-06-23-identity-externalauth-extraction-design.md` — BYO-user-store extraction (0.6.6)

**Modules & neutral capabilities**

- ✅ `2026-06-17-themia-storage-design.md` (0.5.3) · `2026-07-14-storage-public-url-design.md` — permanent public URLs (0.9.0, coord #0022)
- ✅ `2026-06-21-themia-pdf-neutral-core-design.md` (0.6.0) · `2026-07-07-themia-modules-pdf-design.md` (0.7.0)
- ✅ `2026-06-22-themia-notifications-design.md` — multi-channel dispatcher (0.6.2 neutral core, 0.6.3 module)
- ✅ `2026-06-24-themia-export-design.md` (0.6.8) · `2026-06-27-themia-modules-export-design.md` — async/scheduled export (0.6.9)
- ✅ `2026-07-31-themia-messaging-persistence-design.md` · `2026-07-31-themia-messaging-hmac-transport-design.md` · `2026-08-02-themia-messaging-identity-design.md` — outbox/inbox, HMAC transport, one service identity (0.11.0, coord #0050)
- ✅ `2026-08-04-themia-challenges-design.md` — one-time secrets, one core (0.12.0)
- ✅ `2026-09-06-themia-audit-design.md` — activity + authentication event log (0.23.0; entity change log scoped for 0.24.0 in §15)
- ✅ `2026-09-08-themia-geo-design.md` (0.24.0; no module, supersedes the `Themia.Modules.Geo` row in §B)
- ✅ `2026-09-08-themia-ai-design.md` (0.24.0; no module, supersedes the `Themia.Modules.AI` row in §B)

**Shipped without a standalone spec** — `Themia.AspNetCore.DataProtection` (0.10.0, coord #0042),
`Themia.PromptPay` (0.14.0), `Themia.Totp` (0.18.0), `Themia.WebAuthn` (0.20.0), `Themia.Imaging` (0.21.0).

## Identity JWT slice (0.5.1 — 2026-06-15)

`Themia.Modules.Identity.AspNetCore` (net10.0) is the HTTP/JWT layer on top of the 0.5.0 Identity
core. Key structural decisions:

- **Package split is hard.** JWT issuance and JwtBearer validation live entirely in
  `Themia.Modules.Identity.AspNetCore`, NOT in `.Abstractions` — this keeps `.Abstractions` free
  of `Microsoft.IdentityModel.*` / `System.IdentityModel.Tokens.Jwt` (which `Themia.AspNetCore`
  already hosts). `IJwtSigningCredentialsProvider` and `JwtOptions` live in `.AspNetCore`.
- **`RefreshTokenService` is in the Identity CORE** (`Themia.Modules.Identity`), beside
  `UserTokenService`, reusing the internal `IdentityScope` (DbContext/connection + scope guard). It
  runs on both EF Core and Dapper data peers. Only the HTTP-facing pieces (endpoint routing, bearer
  validation, `IAuthenticationFlow`) are in `.AspNetCore`.
- **`refresh_tokens` is a parent-keyed child table** (no `tenant_id` column): the token row
  references `identity.users.id`; tenant isolation is enforced at the service layer (load user +
  validate tenant in the same operation), not by a DB column predicate.
- **Rotating refresh tokens with token-family reuse-detection.** On every refresh, the service
  rotates the presented token and issues a successor in the same family. A reuse attempt
  (presenting an already-consumed token) invalidates the entire family, forcing re-login.
- **`RefreshTokenLifetime` lives in `IdentityModuleOptions`** (core options, not AspNetCore
  options), because the core service owns token creation and must enforce TTL.
- **Anti-enumeration login.** `AuthenticationFlow.LoginAsync` runs an argon2id dummy hash on
  not-found / inactive / locked-out paths, so all failure modes take the same wall-clock time.
- **`IAuthenticationFlow` + `IAuthenticationHooks`** are DI-replaceable seams. Hosts that need
  custom login orchestration (e.g. 2FA, LINE login later) replace only the affected interface.

**Known follow-ups (0.5.1):**
- Concurrent double-use of a single refresh token is not yet guarded by an explicit transaction /
  compare-and-set consume. `RefreshToken` is a plain POCO with no concurrency token, so two
  simultaneous refreshes of the same token could both rotate successfully. This is acceptable at
  current scale; a future hardening could add optimistic concurrency or a transactional consume.

## Decisions

**Resolved (2026-06-01):**
1. ⚠️ **Data layer** — EF-default (canonical) + sanctioned **read-only** Dapper escape-hatch.
   **SUPERSEDED by #6 (2026-06-11).** See Data layer §.
2. ✅ **Sequences** — port `ISequenceProvider` into `Themia.Framework.Data` (tenant-aware,
   table-based, 3-DB, separate-tx semantic) + optional formatter. See §F.
3. ✅ **Tooling** — move to Themia as a build-time family (`Themia.SourceGenerator` +
   `Themia.Analyzers` + `.CodeFixes` + `.Generators.Abstractions`), merging Zenity mediator-gen.
   **`.CodeFixes` was deferred at 0.2.0 and has never been built** — the shipped family is the other
   three, diagnostic-only. See §E.
4. ✅ **Module naming** — capability-named (`Scheduling`, `ExceptionLogging`), applied consistently.
5. ✅ **Phase-1 module set** — **Scheduling, ExceptionLogging, Identity, Storage** — all four capabilities
   shipped. ExceptionLogging shipped as the neutral `Themia.Exceptional` family with **no module
   package** (§B); the other three have modules.

**Resolved (2026-06-11):**
6. ✅ **Data-access peers & schema authority** (supersedes #1) — EF Core and Dapper are **selectable
   first-class peers** (one app-wide; modules code to `Data.Abstractions`); **FluentMigrator is the
   single schema/DDL authority** for all framework-owned tables (no `dotnet ef migrations add`);
   **gate on Dapper-as-peer = tenant-isolation parity with EF**, enforced by making the raw-connection
   bypass conspicuous + an analyzer build-time rule. See Data-access peers & schema authority §.

**Resolved (2026-06-15):**
7. ✅ **Identity JWT package split** — JWT/HTTP pieces in `Themia.Modules.Identity.AspNetCore`
   (net10.0); `RefreshTokenService` in the Identity core; `.Abstractions` stays free of
   `Microsoft.IdentityModel.*`. Refresh tokens use rotating families with reuse-detection;
   tenant isolation enforced at service layer (no `tenant_id` column on `refresh_tokens`).
   See Identity JWT slice §.

_All decisions resolved. **#6 data-layer roadmap (revised 2026-06-12):** **0.4.5** EF SQL Server
provider ✅ → **0.4.6** FluentMigrator-authority **foundation** (neutral `Themia.Data.Migrations`
shared runner + migrate Exceptional onto it) → **0.4.7** Scheduling EF→FM (PostgreSQL + SQL Server) →
**0.4.8** **persistent Quartz** (`AdoJobStore` default + `qrtz_*` per-engine FM schema + System.Text.Json
serializer) → **0.4.9** raw-connection + `DbSet.Find` analyzer gate. **Deferred:** EF MySQL provider + EF concurrency-seam refactor (blocked on Pomelo's
EF Core 10 build — Oracle's `MySql.EntityFrameworkCore` 10.x declined); per-provider concurrency /
framework-column DDL helpers (until a consuming module); FluentMigrator 6→8 (FM 8 broke `IfDatabase`)._
