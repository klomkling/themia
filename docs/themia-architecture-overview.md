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
| `Themia.Framework.Data.EFCore` | Framework.Data.EFCore | EF Core + tenant isolation + audit + provider abstraction (**a first-class data-access peer** — see DECISION #6) |
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
| `Themia.Modules.Identity` (+ `.Abstractions`) | ezy-assets `Jwt/Authentication/RoleAccess/TenantContext/LineLogin` + claims/policies + Zenity Identity.Example | **✅ built** (0.5.0 — tenant-aware user/role/claim store, argon2id, `ICurrentUser`, EF+Dapper, PostgreSQL+SQL Server FM schema) |
| `Themia.Modules.Identity.AspNetCore` | ezy-assets JWT + authentication flows | **✅ built** (0.5.2 — external/OAuth login: pluggable providers + Google/LINE, `AddThemiaExternalAuth`, `MapIdentityExternalAuthEndpoints`; Facebook/Microsoft/Telegram deferred additive providers — on top of 0.5.1 JWT issuance, rotating refresh tokens, `IAuthenticationFlow`, `MapIdentityAuthEndpoints`) |
| `Themia.Modules.Storage` | **ezy-assets** S3/Local + **Idevs** `CloudUploadStorage` + **PowerACC** ClamAV scan | ✅ **built** (0.5.3 — Local + S3/R2 backends, tenant-aware metadata + quota, EF+Dapper, PostgreSQL+SQL Server FM schema) |
| `Themia.Modules.Notifications` | ezy-assets `NotificationDispatcher`/Email/OTP/`Sms2Pro` | ⬜ to-spec |
| `Themia.Modules.Pdf` | **ezy-assets** Contract/Proposal PDF + **Idevs** `PdfOptionsBuilder`/PuppeteerSharp + PowerACC reporting | ⬜ to-spec |
| `Themia.Export` + `Themia.Export.Excel` | **Idevs** `IReportBaseModel`/`IdevsExportRequest`/ClosedXML (Excel), de-Serenity-ized | ✅ **built** (0.6.8 — two stateless neutral cores: typed columns, CSV + xlsx, computed summary rows; no tenant module — the transform is stateless) |
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

**Per-provider concurrency token** is the cross-cutting follow-up (the `ApplyConcurrencyTokens`
landmine in `ThemiaDbContext`): Postgres `xmin` (no DDL), SQL Server `rowversion`, MySQL an
app-updated token — FluentMigrator provisions the right column per engine, and the Dapper layer
needs a matching concurrency story (it has none yet). _Deferred: no framework table uses
`IConcurrencyAware` yet, so the per-engine concurrency DDL helper is built only when a consuming
module first needs it; the EF concurrency-seam refactor rides with the EF MySQL provider._

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
- **Phase 1 — Core cross-cutting:** Scheduling ✅, ExceptionLogging ✅, **Identity** ✅, Storage ✅
  (+ multi-DB SqlServer/MySql/Postgres baseline).
- **Phase 2 — Productivity:** Notifications, Pdf, Export.
- **Phase 3 — Advanced:** Geo, AI, Audit; Sequences EF-port; SourceGenerator/analyzer merge.
- **Ongoing — Strangler:** migrate Idevs.Net.CoreLib's Serenity-free infra into Themia per module.

## Specs index

- ✅ `docs/superpowers/specs/2026-06-01-themia-release-strategy-design.md` (versioning + build order)
- ✅ `docs/superpowers/specs/2026-06-01-themia-quartz-scheduling-design.md`
- ✅ `docs/superpowers/specs/2026-06-01-themia-exceptional-design.md`
- ✅ `docs/superpowers/specs/2026-06-14-themia-identity-core-design.md` (Identity core — 0.5.0)
- ✅ `docs/superpowers/specs/2026-06-15-themia-identity-jwt-design.md` (Identity JWT — 0.5.1)
- ✅ `docs/superpowers/specs/2026-06-17-themia-storage-design.md` (Storage — 0.5.3) · `docs/superpowers/plans/2026-06-17-themia-storage-0.5.3.md`
- ⬜ Phase 0 framework rename (`0.2.0`) — own spec when started
- ⬜ Storage, Notifications, Pdf, Export, … (one spec each, this catalog as parent)

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
   See §E.
4. ✅ **Module naming** — capability-named (`Scheduling`, `ExceptionLogging`), applied consistently.
5. ✅ **Phase-1 module set** — **Scheduling, ExceptionLogging, Identity, Storage** (the two
   specced + Identity + Storage). Identity + Storage next to be specced.

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
