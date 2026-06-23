# Migration Guide

Upgrade notes and breaking-change guidance between **Themia** versions. Every
**(breaking)** entry in [CHANGELOG.md](CHANGELOG.md) has a matching section here
with the *why* and concrete upgrade steps.

## How to read this guide

- Sections are ordered **newest first**, headed by the version that introduced the change.
- Each entry states: **What changed**, **Why**, and **How to upgrade** (before → after).
- Non-breaking changes are *not* listed here — see the CHANGELOG.

## 0.6.6

### External-auth + JWT-issuance types extracted into two new packages

**What changed:** the external-OAuth/OIDC and JWT access-token types moved out of
`Themia.Modules.Identity.AspNetCore` into two new packages — `Themia.Modules.Identity.Tokens.AspNetCore`
(JWT issuance) and `Themia.Modules.Identity.ExternalAuth.AspNetCore` (external login) — each depending only
on `Themia.Modules.Identity.Abstractions`. The `AuthResponse` response record moved down to the
Abstractions package. All affected public types changed namespace:

| Old (`Themia.Modules.Identity.AspNetCore.*`)        | New                                                            |
| --------------------------------------------------- | ------------------------------------------------------------- |
| `…Tokens.*` (e.g. `AccessTokenService`)             | `Themia.Modules.Identity.Tokens.AspNetCore.*`                 |
| `…Signing.*`                                        | `Themia.Modules.Identity.Tokens.AspNetCore.*`                 |
| `…Options.JwtOptions`                               | `Themia.Modules.Identity.Tokens.AspNetCore.*`                 |
| `…Authentication.AuthTokenIssuer`                   | `Themia.Modules.Identity.Tokens.AspNetCore.*`                 |
| `…External.*` (e.g. `OidcExternalAuthProvider`)     | `Themia.Modules.Identity.ExternalAuth.AspNetCore.*`           |
| `…Endpoints.*` (external endpoints)                 | `Themia.Modules.Identity.ExternalAuth.AspNetCore.*`           |
| `…Options.ExternalAuthOptions`                      | `Themia.Modules.Identity.ExternalAuth.AspNetCore.*`           |
| `…DependencyInjection.ExternalAuth*`                | `Themia.Modules.Identity.ExternalAuth.AspNetCore.*`           |
| `…Endpoints.AuthResponse`                           | `Themia.Modules.Identity.Abstractions.Authentication.AuthResponse` |

**Why:** to let an adopter consume JWT issuance and/or external login without taking a dependency on the
full Identity user-store stack — external login in particular now works over a bring-your-own
`IExternalLoginService` with no `IUserService`.

**How to upgrade:**

- **Bundled consumers** (you already reference `Themia.Modules.Identity.AspNetCore`) — **update `using`
  directives only.** That package re-references both new packages, so every moved type is still available
  at runtime; only the namespaces changed. **One exception if you use external login:** the external-auth
  flow is no longer auto-registered by `AddThemiaIdentityAspNetCore` — you must add `AddThemiaExternalAuth()`
  yourself (it was previously wired implicitly). If you map `MapIdentityExternalAuthEndpoints` without it,
  the endpoint resolves no `IExternalAuthenticationFlow` and fails on the first request rather than at
  startup, so also call `ValidateThemiaExternalAuth()` during startup to fail-fast on a missing seam.
- **Bring-your-own (BYO) adoption** — reference the new package(s) directly and:
  - call `AddThemiaIdentityTokens` (Tokens) and/or `AddThemiaExternalAuth` (ExternalAuth);
  - register `IExternalLoginService`, `IRefreshTokenService`, and `IClaimsPrincipalFactory`;
    `IAccessTokenService` is defaulted by the Tokens package (override only if you need custom issuance);
  - or use the provider/registry directly to obtain a validated `ExternalIdentity`;
  - call `ValidateThemiaExternalAuth()` at startup to fail-fast on a missing external-only seam.

### Microsoft.IdentityModel.* bumped to 8.19.1 (whole family, pinned as a unit)

**What changed:** the `Microsoft.IdentityModel.*` family — `Protocols`, `Protocols.OpenIdConnect`,
`Tokens`, `JsonWebTokens`, `Logging`, and `System.IdentityModel.Tokens.Jwt` — moved from 8.0.1 to
**8.19.1**, and `OidcExternalAuthProvider`'s key-rotation recovery was reworked for the new behavior.

**Why:** the family must be version-consistent, and `JwtBearer 10.0.9` only pulls 8.0.1 transitively, so
Themia pins the family explicitly to override it. IdentityModel 8.x also **rate-limits
`ConfigurationManager.RequestRefresh()`** (a refresh-flooding guard); the old "force-refresh and retry in
the same request" became a no-op until the refresh interval elapsed, so a token signed by a freshly
rotated IdP key failed to validate. The provider now does a **direct one-shot metadata/JWKS fetch** on a
rotation signature-failure (bypassing the cooldown) and retries once — rotation still recovers within the
same request. (That path is only reachable after a successful authorization-code exchange, so it is not an
unauthenticated refresh vector.)

**How to upgrade:**

- **No action** for normal use — external-login behavior is unchanged; rotation recovery still works.
- **If you reference `Microsoft.IdentityModel.*` directly** in your own project, align to **8.19.1** (bump
  the whole family together — a split version breaks token validation). Dependabot now groups them
  (`identitymodel`); review such bumps against your ASP.NET Core / `JwtBearer` version.

## 0.6.4

### Upgrade straight to 0.6.4 — do not use 0.6.3

**What changed:** the packages published as **0.6.3 are incomplete** and should be skipped. A
release-pipeline race published 0.6.3 from its original commit *before* two fixes (below) merged,
then the corrected release runs self-skipped because the `v0.6.3` tag already existed. 0.6.4 is
0.6.3 *as intended*.

**Why:** the 0.6.3 publish job waited in the `nuget` environment approval gate while
`Themia.Modules.Notifications` follow-ups merged to `main`; when it was approved it built the stale
tagged commit, and the later push-triggered runs hit the "tag already exists → skip" guard. NuGet
versions are immutable, so the fix is a new version, not a re-publish.

**How to upgrade:**

- If you installed any `Themia.*` **0.6.3** package, bump to **0.6.4** (same API surface; no code
  changes). 0.6.3 is recommended for unlisting on nuget.org.
- Nothing else is required for the Notifications API itself — 0.6.4 only adds the MySQL deadlock fix
  and the FluentMigrator bump described next.

### FluentMigrator 7.2.0 → 8.0.1

**What changed:** the `FluentMigrator` core and the `FluentMigrator.Runner.Postgres` /
`FluentMigrator.Runner.MySql` / `FluentMigrator.Runner.SqlServer` packages moved to **8.0.1** (a major
version).

**Why:** stay current with the migration engine; the split per-runner Dependabot PRs could never land
the shared-version bump on their own (each moved the same `FluentMigrator` coordinate and conflicted),
so the four packages are bumped together. Validated against every FluentMigrator-backed integration
suite (Data.Migrations, Scheduling, Exceptional, Notifications) on PostgreSQL/MySQL/SQL Server.

**How to upgrade:**

- **No action** if you run migrations through Themia (`ThemiaMigrations.Run(...)`,
  `NotificationsModule.InitializeAsync`, the Scheduling/Exceptional modules) — the bump is transitive
  and behavior-compatible across the supported engines.
- **Only if you reference the `FluentMigrator*` packages directly** in your own project (e.g.
  hand-authored migrations or a custom runner): align your reference to `8.0.1` and review the
  [FluentMigrator 8.0 release notes](https://github.com/fluentmigrator/fluentmigrator/releases) for
  any of your own usages.

## 0.4.9

### Themia analyzers now run in adopter builds

**What changed:** referencing any `Themia.Framework.Data.*` package now brings the `Themia.Analyzers`
rules into your build: THEMIA103/104 (tenant-isolation gates) and the pre-existing THEMIA101/102 hygiene
rules. They are **Warnings**, not errors.

**Why:** DECISION #6 — tenant isolation should hold by construction. The two gates flag the raw-connection
and `DbSet.Find` bypasses at build time so the safe path is inescapable without an explicit, reviewable
suppression.

**How to upgrade:**

- No action required if you build with warnings as warnings.
- To silence a rule globally, add to `.editorconfig`: `dotnet_diagnostic.THEMIA104.severity = none`
  (or `= error` to enforce it harder), or configure the whole group via
  `dotnet_analyzer_diagnostic.category-Themia.Isolation.severity = …`.
- For a one-off deliberate bypass, suppress at the call site with a justification:
  `#pragma warning disable THEMIA103` or `[SuppressMessage("Themia.Isolation", "THEMIA103", Justification = "…")]`.
- The guarded alternatives are `ITenantQueryFactory.For<T>()` (Dapper) and `DbContext.FindAsync<T>()` /
  `IReadRepository.GetByIdAsync()` (EF).

## 0.4.8

### Scheduling module now owns a persistent Quartz scheduler by default

**What changed:** `Themia.Modules.Scheduling` registers and starts a persistent AdoJobStore scheduler (the
`qrtz_*` tables in a `quartz` schema; System.Text.Json serializer; `UseProperties = true`). Previously the host
supplied the `IScheduler`.

**Why:** scheduled jobs must survive restarts; FluentMigrator owns the `qrtz_*` schema (DECISION #6).

**How to upgrade:**

- Ensure an EF provider is registered (`AddThemiaPostgres`/`AddThemiaSqlServer`) and call the module's
  `InitializeAsync` **before** running the host — the `qrtz_*` tables must exist before the scheduler starts.
- JobDataMap is stored as string key-values (`UseProperties = true`) — job data must be string-serializable.
- To keep managing your own scheduler, set `SchedulingModuleOptions.UsePersistentStore = false`; the module then
  registers no scheduler and the dashboard resolves your host-supplied `IScheduler` as before.
- The scheduler uses the `Default` connection (process-wide, never tenant-routed). SQL Server + PostgreSQL only.

## 0.4.7

### Scheduling module: schema via FluentMigrator + requires an EF provider

**What changed:** `Themia.Modules.Scheduling` applies its schema with FluentMigrator at `InitializeAsync`
(through `Themia.Data.Migrations`) instead of EF Core migrations, and is now provider-agnostic over
PostgreSQL and SQL Server. It resolves the active `IDatabaseProvider` for both the EF provider and the
migration engine.

**Why:** FluentMigrator is the single schema authority (DECISION #6); the module is no longer PostgreSQL-only.

**How to upgrade:**

- Ensure an EF provider is registered before the module initializes — `AddThemiaPostgres<…>(…)` or
  `AddThemiaSqlServer<…>(…)`. Without one, the module throws at startup.
- Stop running `dotnet ef database update` for the scheduling context; the schema is applied automatically
  on startup.
- **Existing PostgreSQL databases:** the FluentMigrator migration is **idempotent** — it skips the
  `scheduling` schema and any `execution_history` / `scheduler_stats` table that already exists, so a database
  carrying the pre-0.4.7 EF-created tables adopts them in place and simply records the FluentMigrator version
  (it does **not** drop or recreate your data). On a fresh database it creates the tables. The table shapes are
  unchanged. (Note: FluentMigrator names the primary-key constraints with its own defaults rather than the EF
  `pk_*` names — cosmetic only; queries are unaffected.)
- SQL Server is now supported.

## 0.4.6

### `AddThemiaExceptionalProvider` takes a `MigrationEngine`

**What changed:** the provider-author extension `AddThemiaExceptionalProvider` (in `Themia.Exceptional`)
replaced its `Action<IMigrationRunnerBuilder> configureRunner` + `string databaseDisplayName` parameters
with a single `Themia.Data.Migrations.MigrationEngine engine`.

**Why:** the FluentMigrator runner moved into the neutral `Themia.Data.Migrations` package so every
neutral core and framework module shares one runner (DECISION #6). The engine enum replaces the
per-call runner-builder callback.

**Who is affected:** only third parties that call `AddThemiaExceptionalProvider` directly to back a
custom dialect. Adopters using `AddThemiaExceptionalPostgres` / `…MySql` / `…SqlServer` are unaffected.

**How to upgrade:**

- Before:
  ```csharp
  services.AddThemiaExceptionalProvider(
      dialect: myDialect,
      configure: opt => opt.ApplicationName = "App",
      configureRunner: rb => rb.AddPostgres(),
      connectionString: connString,
      databaseDisplayName: "PostgreSQL");
  ```
- After:
  ```csharp
  using Themia.Data.Migrations;

  services.AddThemiaExceptionalProvider(
      dialect: myDialect,
      configure: opt => opt.ApplicationName = "App",
      engine: MigrationEngine.Postgres,
      connectionString: connString);
  ```

## 0.4.5

### `AddThemiaPostgres` moved to `Themia.Framework.Data.EFCore.PostgreSql`

**What changed:** the core EF package (`Themia.Framework.Data.EFCore`) is now provider-agnostic.
`PostgresDatabaseProvider` and `AddThemiaPostgres` live in the new
`Themia.Framework.Data.EFCore.PostgreSql` package; core no longer references Npgsql.

**Why:** per-engine provider packages (mirroring the Dapper layer, DECISION #6) — consumers pull
only the engine they use instead of every provider's dependencies.

**How to upgrade:**

- Before:
  ```csharp
  // package: Themia.Framework.Data.EFCore
  using Themia.Framework.Data.EFCore.Extensions;
  services.AddThemiaPostgres<AppDbContext>(configuration);
  ```
- After:
  ```csharp
  // packages: Themia.Framework.Data.EFCore.PostgreSql (core comes transitively)
  using Themia.Framework.Data.EFCore.PostgreSql;
  services.AddThemiaPostgres<AppDbContext>(configuration);
  ```

### `AddThemiaDbContextWithProvider` removed

**What changed:** the string-name provider factory (`AddThemiaDbContextWithProvider(configuration,
"postgres")`) was removed from core.

**Why:** core can no longer construct provider types it does not reference; each provider package
ships its own type-safe entry point.

**How to upgrade:** call the per-engine extension directly — `AddThemiaPostgres<TContext>(…)`
(`Themia.Framework.Data.EFCore.PostgreSql`) or `AddThemiaSqlServer<TContext>(…)`
(`Themia.Framework.Data.EFCore.SqlServer`).

### App-table columns are no longer forced to snake_case

**What changed:** the providers no longer apply `UseSnakeCaseNamingConvention()` to the whole model
by default. Themia's framework columns (`id`, `tenant_id`, `created_at`, `is_deleted`,
`row_version`, …) are now explicitly mapped to snake_case in `ThemiaDbContext` regardless; your own
entities' columns follow the EF provider default (property name as-is — PascalCase on SQL Server).

**Why:** Themia owns the naming of *its* columns (parity with the Dapper layer and one
FluentMigrator schema across engines) but should not dictate the adopter's app-table naming.

**How to upgrade:**

- If your existing PostgreSQL schema has snake_case **app** columns (the previous forced behavior),
  reference `EFCore.NamingConventions` in your app and re-apply the convention via the registration
  delegate:
  ```csharp
  services.AddThemiaPostgres<AppDbContext>(
      configuration,
      configureOptions: o => o.UseSnakeCaseNamingConvention());
  ```
- New apps (and SQL Server apps wanting idiomatic PascalCase) need no change — no global convention
  is applied by default, and the provider packages no longer depend on `EFCore.NamingConventions`.

## Template

````markdown
## x.y.z

### <short title of the breaking change>

**What changed:** …

**Why:** …

**How to upgrade:**

- Before:
  ```csharp
  // old usage
  ```
- After:
  ```csharp
  // new usage
  ```
````
