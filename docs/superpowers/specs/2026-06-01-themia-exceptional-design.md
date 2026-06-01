# Themia Exception Logging — Design

> Target **Themia.\*** brand (framework rename = separate task). **Spec 2 of 2.**

## Goal

A **framework-neutral exception-logging** library, `Themia.Exceptional`, that **captures,
persists, and surfaces** errors for the new Themia framework + apps, across **three relational
engines in Phase 1: SQL Server, MySQL (incl. MariaDB), PostgreSQL** (extensible later).

Layers:
- `Themia.Exceptional` — neutral engine: capture (Serilog sink + enricher + request-body
  middleware) → custom **Dapper store with dialect strategy** → relational persistence + dashboard.
- `Themia.Exceptional.SqlServer / .MySql / .PostgreSql` — one driver + one dialect each.
- `Themia.Modules.ExceptionLogging` — Themia module (`IThemiaModule`), tenant-aware, migration.

**PowerACC is not a driver** (same as spec 1). A Serenity adapter (`Idevs.Net.CoreLib.Exceptional.*`)
that exposes the engine through the Serenity Exception Log page is **deferred / optional**, built
only if PowerACC migrates.

**Scope correction (MAJOR 5):** the **typed-exception hierarchy + ProblemDetails middleware are
NOT in this package.** They are an API-error-contract concern → they live in
**`Themia.AspNetCore`** (see that section). `Themia.Exceptional` is capture+persist+dashboard only.

## Engine = custom Dapper store (MAJOR 3 — contradiction resolved)

The engine is a **custom Dapper store + `IExceptionalSqlDialect` strategy** with **one consistent
schema across all three engines** — it is **not** built on `StackExchange.Exceptional`'s own
store. (StackExchange.Exceptional appears *only* in the deferred Serenity adapter, whose fork
`ErrorStore` powers the Serenity admin page.) This gives one schema + one rollup/query layer for
the 3 DBs, which a per-DB Serilog sink (ezy-assets' Postgres-only approach) cannot.

## Target frameworks (layered)

- `Themia.Exceptional` + `.SqlServer/.MySql/.PostgreSql` (engine, cross-framework) →
  **`net8.0;net10.0`** (must include net8 to keep PowerACC reuse open; all drivers support net8).
- `Themia.Modules.ExceptionLogging` → **`net10.0`**.

## Architecture

```
Themia.AspNetCore         # (separate package) typed exceptions + ProblemDetails middleware
  ├─ ThemiaException + Validation/NotFound/Conflict/Forbidden/Unauthorized/ExternalService + ErrorCodes
  └─ ProblemDetailsMiddleware (RFC-7807; ported from ezy-assets, app-domain stripped)

Themia.Exceptional                  # NEUTRAL engine — net8.0;net10.0 — ASP.NET Core + Serilog + Dapper + FluentMigrator
  ├─ IExceptionStore + ExceptionStoreEngine     # Dapper rollup/get/protect/delete/purge
  ├─ IExceptionalSqlDialect                      # SQL + connection + GUID-param strategy
  ├─ HttpContextEnricher                         # Serilog; SCRUBS Cookie/Authorization
  ├─ SeExceptionalSerilogSink                    # Exception!=null && Level>=Error → store
  ├─ RequestBodyLoggingMiddleware
  └─ ExceptionLogMigration                       # one FluentMigrator def, rendered per provider

Themia.Exceptional.SqlServer / .MySql / .PostgreSql     # driver + dialect + Add… (PHASE 1)

Themia.Modules.ExceptionLogging     # net10.0 — engine(.provider) + Themia.Framework.*
  ├─ ExceptionLoggingModule : ThemiaModuleBase
  ├─ tenant scoping (TenantId column + filter)
  ├─ Themia exception dashboard (custom page over IExceptionStore — see Dashboard)
  └─ InitializeAsync → migration

[DEFERRED] Idevs.Net.CoreLib.Exceptional.*   # Serenity adapter — Serenity-fork ErrorStore
  └─ delegates to ExceptionStoreEngine; Serenity admin page via KnownStoreTypes. Built only if PowerACC migrates.
```

## `Themia.AspNetCore` — typed exceptions + ProblemDetails (standalone neutral package)

**Standalone, framework-neutral** (MAJOR 1 fix): does **not** depend on `Themia.Framework.*`,
targets **`net8.0;net10.0`**, and is **shippable before the P0 rename** + usable by any ASP.NET
Core app including PowerACC. It is *not* part of (and does not modify) the framework's own
`Themia.Framework.AspNetCore`.

Ported from ezy-assets `ExceptionMiddleware` + exception hierarchy, **app-domain stripped**
(MINOR 8): drop EzyAssets-specific codes like OTP-cooldown/`RetryAfterSeconds`-for-OTP; keep the
generic shape (status mapping, `traceId`/`errorCode`/metadata extensions, `application/problem+json`).
Remove the source's `Console.Error.WriteLine` fallback — `ILogger` only. Exposed as
`app.UseThemiaProblemDetails()`. Usable without any DB/exception-store dependency.

## `Themia.Exceptional` (neutral engine)

- `ExceptionStoreEngine` (Dapper): rollup-then-insert by `ErrorHash` within `RollupPeriod`
  (default 10 min), `FullJson` store/rehydrate (**System.Text.Json**), get/list/count, protect,
  soft/hard delete, purge. One connection per method (analyzer-clean). Delegates SQL +
  GUID-param to `IExceptionalSqlDialect`.
  - **Paged + filtered query (MINOR 4):** the dashboard needs `IExceptionStore` to expose a
    **paged, filterable** list (by date / app / tenant / search), not just the capped
    `GetAllErrors` (MaximumDisplayCount = 500). Add this to the query API before the dashboard.
- **Staged delivery:** the 3-DB dialect engine is the heaviest piece. Ship **one provider
  end-to-end first** (prove the engine + migration + tests), then add the other two — the dialect
  strategy already accommodates this; avoids a big-bang across three engines at once.
- Capture: `SeExceptionalSerilogSink` + `HttpContextEnricher` (**scrub `Cookie`/`Authorization`**
  + configurable extra keys) + `RequestBodyLoggingMiddleware`. Sink filter
  `Exception != null && Level >= Error`; fault-only continuation to Serilog `SelfLog`.
- Schema (FluentMigrator, one def per provider): `Exceptions` table — identity `Id` PK, `GUID`
  (CHAR(36)/uniqueidentifier/uuid), app/category/machine/type/host/url/method/message/source
  (truncated), `FullJson` (provider max-text), `ErrorHash`/`StatusCode`/`DuplicateCount`,
  creation/lastlog/deletion dates, index `(ApplicationName, ErrorHash, CreationDate)` + `DeletionDate`.

## Provider packages (Phase 1)

| Package | Driver | Dialect (atomic rollup + UTC + GUID) |
|---|---|---|
| `.SqlServer` | `Microsoft.Data.SqlClient` | `UPDATE…SET @newGUID=GUID WHERE Id IN (SELECT TOP 1…)`→`SELECT @newGUID`; `GETUTCDATE()`; native Guid |
| `.MySql` | `MySqlConnector` | `GUID=(@newGUID:=GUID)…LIMIT 1`; `UTC_DATE()`; GUID string; `AllowUserVariables`. **Covers MariaDB** |
| `.PostgreSql` | `Npgsql` | `WITH updated AS (UPDATE…WHERE Id=(SELECT…LIMIT 1) RETURNING GUID) SELECT…`; `now() at time zone 'utc'`; native Guid |

Later phases (own specs): SQLite, Oracle — add package + dialect, no engine change.

## `Themia.Modules.ExceptionLogging`

`ExceptionLoggingModule : ThemiaModuleBase`, deps `Themia.Framework.Core` + chosen
`Themia.Exceptional.<provider>`. `ConfigureServices`: register store/dialect, sink, enricher,
request-body middleware. **Serilog integration (MINOR 3):** the sink **plugs into the app's
existing Serilog pipeline** (the one `Themia.Logging` configures) — it does **not** create a
competing logger. `InitializeAsync`: run migration. **Hard dependency:** needs the P0 rename.

**Tenant scoping (optional — confirm):** unlike scheduling history, exceptions occur in tenant
request contexts, so a nullable `TenantId` column + filter is *plausibly* useful (tenant-admin
sees own errors; platform-admin sees all). Kept **optional** pending a decision on whether the
exception view is per-tenant or platform-admin-only. Default Phase 1: stamp `TenantId` when a
tenant context exists, dashboard defaults to platform-admin (all tenants).

## Dashboard
Custom Themia exception list/detail page over `IExceptionStore` (tenant-filtered) in the module —
fits the Themia admin UI (year-1 phase-4). The upstream StackExchange.Exceptional `/exceptions`
page is **not** used (we don't use its store). Serenity (if/when adopted) keeps its own admin page.

## Standards / Testing / Verification
`PublicAPI` tracking; full XML docs; analyzer-clean (`IDEVS`/`THEMIA` rules — one-connection-per-method,
no in-method swallow-and-rethrow). Contract tests (Testcontainers) across **SQL Server / MySQL /
MariaDB / PostgreSQL**: insert, rollup/dedup by `ErrorHash`, GUID round-trip, `FullJson` rehydrate,
soft/hard delete, protect, count, migration. Unit: enricher scrub, sink filter, `Truncate`. Plus
ProblemDetails status-mapping tests in `Themia.AspNetCore`. Build net8.0 + net10.0 (engine).

## Open items
1. Dashboard scope (list/detail MVP first).
2. Confirm `RollupPeriod = 10 min` for Themia.
3. Lock the ProblemDetails extension-key contract (`traceId`/`errorCode`/metadata) in `Themia.AspNetCore`.
4. Phase-2 provider list (SQLite? Oracle?).

## Relation to prior specs
Supersedes the Serenity-only `Idevs.Net.CoreLib/.../2026-05-31-exception-log-pipeline-design.md`:
that becomes the **deferred** Serenity adapter (`Idevs.Net.CoreLib.Exceptional.*`) delegating to
this engine. Same neutral-core + module pattern as spec 1.
