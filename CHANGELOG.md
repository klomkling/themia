# Changelog

All notable changes to the **Themia** packages are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
All Themia packages share a **single version** (single-version monorepo); each
released version tags the whole set.

Categories: **Added**, **Changed**, **Deprecated**, **Removed**, **Fixed**, **Security**.
Breaking changes are prefixed **(breaking)** and cross-referenced in [MIGRATION.md](MIGRATION.md).

- **Scope:** this file lists *notable* changes only. The exhaustive per-PR list lives in the
  auto-generated [GitHub Releases](https://github.com/klomkling/themia/releases).
- **Archiving (à la Serenity):** to keep this file readable, entries from **past years** are
  moved out to `docs/changelog/changelog-YYYY.md` and replaced here by a one-line link under
  [Older releases](#older-releases). The current (and most recent) year stays inline.

## [Unreleased]

Hardening pass: unblock cross-assembly consumers of the DI generator, fix two EF Core correctness
issues, and sweep cheap wins across Exceptional / Mediator / tooling.

### Fixed

- `Themia.SourceGenerator` — the generated DI registration class (`Themia.Generated.ThemiaServiceRegistrations`)
  is now `internal`, fixing **CS0121** ambiguity when a consumer references a package that also uses the
  generator (e.g. `Themia.Mediator`) and runs the DI generator itself. Each assembly registers its own services.
- `Themia.Framework.Data.EFCore` — `Find`/`FindAsync` now read the **same tenant source as the runtime query
  filter** (the static `TenantContextAccessor` under `RuntimeTenantAccess`), so a Find can no longer disagree
  with the filter (and leak/hide a cross-tenant row) under non-standard wiring.
- `Themia.Framework.Data.EFCore` — optimistic concurrency on **PostgreSQL** now uses the server-maintained
  `xmin` system column (a `uint` rowversion shadow property), so a conflicting `SaveChanges` correctly throws
  `DbUpdateConcurrencyException` (previously the `byte[]` rowversion mapped to non-server-populated `bytea` and
  never fired).
- `Themia.Exceptional` — `ExceptionHash` includes `Source` and the inner-exception type in its fallback when
  `StackTrace` is null, reducing rollup collisions between distinct same-message errors.
- `Themia.Exceptional` — added an index on the purge predicate `(IsProtected, CreationDate)`, and the migration
  now throws a clear `NotSupportedException` for an unsupported database provider (instead of a silent no-op).

### Changed

- `Themia.SourceGenerator` — the DI registration generator now filters at the syntax level via
  `ForAttributeWithMetadataName` (attribute path) and narrowed syntax predicates with the semantic model
  (marker/registrar paths). This pipeline refactor does not alter generated output — the only output change in
  this release is the `internal` visibility fix listed above under Fixed. **Note:** full incremental-generation *caching* is
  not yet achieved — the pipeline data model still carries Roslyn symbols/syntax nodes across the
  `Collect()`/`Combine()` boundary (non-equatable, roots the compilation), so the output stage re-runs on every
  edit. Output-stage cache equality is tracked as a 0.3.2 follow-up (see Known limitations).
- `Themia.Exceptional` — the **dialect now owns From/To temporal parameter binding**
  (`IExceptionalSqlDialect.AddTemporalFilters` replaces `TemporalFilterDbType`); `ExceptionStoreEngine` takes
  `ExceptionalOptions` (single source for the rollup period).
- `Themia.Mediator` — `MediatorCachingOptions.KnownTypeSuffixes`/`KnownVerbPrefixes` are now
  `IReadOnlyList<string>` (immutable element access); `CacheableAttribute` expiration defaults to `0`
  ("not set") instead of `-1` (`int?` is not a valid attribute-argument type).

### Known limitations (0.3.x backlog)

- **Targeted for 0.3.2 (P3):** `Themia.Exceptional` — SqlServer `datetime2` write precision (Dapper infers
  legacy `datetime` ~3.33 ms on INSERT/rollup); extract a shared internal `AddThemiaExceptionalProvider` helper
  (DI/`RunMigration` triplicated ×3); shared parameterized conformance test harness over `IExceptionalSqlDialect`.
- **Targeted for 0.3.2 (P3):** `Themia.SourceGenerator` — complete DI-generator incrementality. The pipeline
  model (`DiscoveredTypeInfo`) carries `INamedTypeSymbol`/`ClassDeclarationSyntax`/`AttributeData` into the
  output node, defeating cache equality. Fix relocates all semantic analysis into the `transform` and emits
  equatable record types (registration record + a replayable `DiagnosticInfo`); snapshot/diagnostic tests pin
  the unchanged output. All-or-nothing (one symbol in the model defeats the cache), so it is its own task.
- **Deferred (P4):** `Themia.Exceptional` — `ListSql` uses `SELECT *` (project a summary column set together
  with the dashboard); the migration runs synchronously at DI-registration (consider a post-build migrate step).

## 0.3.0 — 2026-06-05

The **`Themia.Exceptional`** family: a framework-neutral exception-logging engine plus PostgreSQL,
MySQL/MariaDB, and SQL Server dialects (each proven against the real engine via Testcontainers).

### Added

- `Themia.Exceptional` — framework-neutral exception-logging engine: rollup-aware Dapper store
  (`IExceptionStore`/`ExceptionStoreEngine`), `IExceptionalSqlDialect` strategy, FluentMigrator schema,
  Serilog sink + HTTP enricher (scrubs Cookie/Authorization), and an opt-in request-body middleware.
  Request body captured by the middleware is now persisted to a `RequestBody` column on the
  `Exceptions` table and surfaced on `ExceptionEntry.RequestBody`.
- `Themia.Exceptional.PostgreSql` — PostgreSQL dialect (Npgsql) + `AddThemiaExceptionalPostgres(...)`.
  Registers `ExceptionalSerilogSink` and `HttpContextEnricher` as DI singletons for the host to wire
  into its own Serilog `LoggerConfiguration`; this package does not configure the global logger itself.
- `Themia.Exceptional.MySql` — MySQL/MariaDB dialect (MySqlConnector) + `AddThemiaExceptionalMySql(...)`.
- `Themia.Exceptional.SqlServer` — SQL Server dialect (Microsoft.Data.SqlClient) + `AddThemiaExceptionalSqlServer(...)`.

### Fixed

- `Themia.Exceptional` — temporal filter parameters (`From`/`To`) now delegate their `DbType` to the
  dialect (`IExceptionalSqlDialect.TemporalFilterDbType`), fixing text-comparison mismatches on SQLite
  and `Kind=Unspecified` timestamp errors on PostgreSQL. All entry timestamps are coerced to `Kind=Utc`
  on write so callers building `ExceptionEntry` with `Kind=Unspecified/Local` no longer throw.
- `Themia.Exceptional` — `HttpContextEnricher` captures `StatusCode` whenever the response code
  is non-200, not only after `Response.HasStarted`.
- `Themia.Exceptional` — `ExceptionalSerilogSink.Emit` writes synchronously; high-throughput hosts
  should wrap it with `Serilog.Sinks.Async`. (Documented in XML remarks, not a behavior change.)

### Known limitations (0.3.x backlog)

- `ExceptionHash` falls back to `Message` when `StackTrace` is null — distinct same-message errors
  from different call sites can be rolled into one row.
- `AddThemiaExceptionalPostgres` runs the FluentMigrator migration synchronously at DI-registration
  time, requiring the database to be reachable at startup. Consider an explicit post-build migrate step.
- `ListSql`/`CountSql` WHERE predicate is duplicated per dialect (and across the 3 dialects). Extract a
  shared predicate fragment with dialect-supplied leaf tokens (quote char, paging syntax, bool literal).
- `ExceptionStoreEngine` exposes the rollup period both through `ExceptionalOptions` (wired by
  `AddThemiaExceptionalCore`) and a redundant constructor parameter; constructing the engine directly
  bypasses the options value. Consider collapsing to a single source.
- The three provider DI extensions + their `RunMigration` are near-identical (only the dialect ctor and
  the `.AddXxx()` runner differ). Extract a shared internal `AddThemiaExceptionalProvider` helper.
- SqlServer write path: Dapper infers legacy `SqlDbType.DateTime` (~3.33 ms) for the `datetime2` timestamp
  columns on INSERT/rollup, losing sub-3 ms precision. A clean fix needs per-parameter `datetime2` typing
  without a process-global Dapper `DateTime` handler.
- `ExceptionLogMigration.Up()` is a whitelist of three `IfDatabase` branches with no default; table + indexes
  are created together per provider, so an unmatched provider produces an empty (no-op) migration and the first
  store call then fails with "Exceptions does not exist". Add a matching branch when adding a dialect (e.g. SQLite,
  Oracle); a migration-time fail-fast for unsupported providers would be a further improvement.
- Integration suites are duplicated per engine (counts drift: Postgres 11 vs MySQL/SqlServer 13). Introduce a
  shared parameterized conformance fixture over `IExceptionalSqlDialect`.
- `ListSql` uses `SELECT *` (pulls `Detail`/`RequestBody` per list row); project a summary column set for
  the dashboard list view. `PurgeSql`'s `(IsProtected, CreationDate)` predicate is unindexed.

## 0.2.0 — 2026-06-05

The complete **Phase 0** framework rename (zenity-v2 → `Themia.*`): build-time tooling, the
framework core, the cross-cutting packages, and the EF Core data + ASP.NET Core host layers.
All packages share this version (single-version monorepo).

### Added

- `Themia.Generators.Abstractions` (`netstandard2.0`) — reusable Roslyn helpers (compilation
  scanner, service-type/lifetime resolvers, deterministic source writer, diagnostics factory +
  reserved diagnostic-ID ranges) shared by the source generator and analyzers.
- `Themia.DependencyInjection` (`net8.0;net10.0`) — DI marker attributes
  (`[Scoped]`/`[Singleton]`/`[Transient]`, init-only, with `ServiceType`/`ServiceKey`/
  `AllowSelfRegistration`), lifetime marker interfaces (`IScopedService<T>` etc.), and
  `IThemiaServiceRegistrar`.
- `Themia.SourceGenerator` (`netstandard2.0`) — reflection-free, compile-time DI registration
  generator emitting `AddThemiaServices(IServiceCollection)` from attributes + markers +
  registrars, including keyed registrations via `ServiceKey`; diagnostics `THEMIA001`–`THEMIA010`.
- `Themia.Analyzers` (`netstandard2.0`) — `THEMIA101` (catch-log-rethrow) and `THEMIA102`
  (sync-over-async wrapped in `Task.FromResult`).
- `Themia.Framework.Core` (`net10.0`) — DDD core: `Entity`/`AuditableEntity`, `ValueObject`,
  `Result`/`Error` + `ResultExtensions`, domain events (`IDomainEvent`/`IDomainEventDispatcher` +
  dispatcher), tenant context (`TenantId`/`TenantContext`/accessor), and the `IThemiaModule`
  module system. (Ported from the canonical Zenity-v2 core.)
- `Themia.Caching` (`net10.0`) — memory + Redis cache providers with JSON/MessagePack
  serialization, a fluent builder, and options (`AddThemiaCaching`).
- `Themia.Logging` (`net10.0`) — Serilog-backed logging with a fluent builder, console/file
  sinks, thread/environment enrichers, and options (`AddThemiaLogging`).
- `Themia.MultiTenancy` (`net10.0`) — tenant resolution stack: `Header`/`Path`/`Default`
  strategies, `InMemory`/`Cached`/`Dapper` catalog stores, `ITenantResolver`, and a fail-closed
  `TenantResolutionMiddleware` that bridges the resolved tenant into both the rich `ITenantAccessor`
  (read-only `Current`; writes via `ITenantSetter`) and the framework's ambient `TenantContextAccessor`
  so the data layer filters on the same tenant. `MultiTenancyBuilder` + validated options
  (`ValidateOnStart`). `TenantInfo.ConnectionString` is redacted from `ToString`/JSON; the Dapper
  catalog query is parameterized, table-name-allowlisted, and engine-portable. Supports both
  shared-DB tenant-filtering and DB-per-tenant (via the per-tenant connection string).
- `Themia.Mediator` (`net10.0`) — CQRS mediator: `IRequest`/`IRequestHandler`, `ICommand`/`IQuery`,
  and pipeline behaviors (`Validation`/`Logging`/`Caching`/`Performance`/`Transaction`). Query
  caching is tenant-scoped with attribute-driven invalidation by type/prefix/scope. Handler
  registration + an `IMediator` dispatcher are generated at compile time by `Themia.SourceGenerator`
  (opt in with `[assembly: GenerateMediatorHandlers]`; handler lifetime via `[SingletonHandler]`/
  `[TransientHandler]`; diagnostics `THEMIA011`–`THEMIA013`).
- `Themia.Services` (`net10.0`) — cross-cutting service taxonomy: the `IService`/`IDomainService`/
  `IInfrastructureService`/`IIntegrationService` markers plus infrastructure-service contracts
  (email, SMS, push, storage, report export, background jobs, secrets, audit, tokens, event bus)
  as forward-seams for future modules. Business-domain contracts deliberately stay out (framework/app
  boundary).
- `Themia.Framework.Data.EFCore` (`net10.0`) — canonical EF Core data layer: the `ThemiaDbContext`
  base with a tenant-isolating global query filter that **fails closed** (a null current tenant
  returns only global rows, never another tenant's), soft-delete, and audit/concurrency stamping; a
  pluggable `IDatabaseProvider` (built-in PostgreSQL via Npgsql + snake-case naming) with DI
  extensions (`AddThemiaPostgres`/`AddThemiaDbContext`). Supports **DB-per-tenant**: when a tenant is
  resolved and carries a connection string (`ITenantAccessor.Current?.ConnectionString`), the provider
  uses it per scope; otherwise — including when no tenant accessor is registered — it falls back to the
  `Default` connection string (shared-DB + tenant filter). (Ported from Zenity-v2.)
- `Themia.Framework.AspNetCore` (`net10.0`) — ASP.NET Core host wiring: `AddThemiaAspNetCore()`
  registers the scoped `ITenantContext`, and `UseThemia()` composes the neutral
  `UseThemiaProblemDetails()` (RFC-7807, outermost) with the `Themia.MultiTenancy` tenant-resolution
  middleware.

## 0.1.0 — 2026-06-02

### Added

- Repository scaffold: `Themia.sln`, `Directory.Build.props` / `Directory.Packages.props`,
  `nuget.config`, and the MIT `LICENSE`.
- CI/CD (GitHub Actions): build & test on `net8.0` + `net10.0`, a separate Testcontainers
  integration workflow, and a NuGet release workflow using **Trusted Publishing (GitHub OIDC)** —
  version read from `Directory.Build.props`, pack the solution, publish + tag + GitHub Release.
- Dependabot (NuGet + GitHub Actions) with **native auto-merge** for non-major and Actions bumps.
- `Themia.AspNetCore` (`net8.0;net10.0`) — framework-neutral typed exception hierarchy
  (`ThemiaException` base + `Validation`/`NotFound`/`Conflict`/`Forbidden`/`Unauthorized`/
  `ExternalService` exceptions) and an RFC-7807 `ProblemDetailsMiddleware` that maps them to
  HTTP statuses and writes `application/problem+json` with `traceId`/`errorCode`/metadata
  extensions, plus the `UseThemiaProblemDetails()` registration extension. Exceptions are
  HTTP-agnostic (the type→status map lives only in the middleware); unknown exceptions return a
  generic 500 without leaking internal details.

## Older releases

_No archived years yet._ As the changelog grows, each past year's releases move to
`docs/changelog/changelog-YYYY.md`, leaving a stub here — for example:

<!--
## 2027

All Themia versions published in 2027 (x.y.z through a.b.c) are in
[changelog-2027.md](docs/changelog/changelog-2027.md).
-->

