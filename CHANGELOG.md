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

