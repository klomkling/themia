# Changelog

All notable changes to the **Themia** packages are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
All Themia packages share a **single version** (single-version monorepo); each
released version tags the whole set.

**Versioning policy (pre-1.0).** Following [SemVer](https://semver.org/spec/v2.0.0.html)'s allowance
that anything may change before 1.0, Themia uses a milestone-based scheme while in `0.x`:

- **MINOR** (`0.x.0`) — a new module/package or a phase boundary (e.g. `0.5.0` = Identity module).
- **PATCH** (`0.x.y`) — backwards-compatible additive features **and** fixes within a milestone.
- **MAJOR** — reserved; breaking changes pre-1.0 are flagged **(breaking)** here and in
  [MIGRATION.md](MIGRATION.md).

At `1.0` this switches to strict SemVer (every backwards-compatible feature → MINOR).

Categories: **Added**, **Changed**, **Deprecated**, **Removed**, **Fixed**, **Security**.
Breaking changes are prefixed **(breaking)** and cross-referenced in [MIGRATION.md](MIGRATION.md).

- **Scope:** this file lists *notable* changes only. The exhaustive per-PR list lives in the
  auto-generated [GitHub Releases](https://github.com/klomkling/themia/releases).
- **Archiving (à la Serenity):** to keep this file readable, entries from **past years** are
  moved out to `docs/changelog/changelog-YYYY.md` and replaced here by a one-line link under
  [Older releases](#older-releases). The current (and most recent) year stays inline.

## [Unreleased]

## [0.6.5] - 2026-06-23

### Changed
- Bumped the **`Microsoft.IdentityModel.*` family to 8.19.1** (Protocols, Protocols.OpenIdConnect,
  Tokens, JsonWebTokens, Logging, and `System.IdentityModel.Tokens.Jwt`), pinned as a unit to override
  the 8.0.1 that `JwtBearer 10.0.9` resolves transitively. Dependabot now groups the family
  (`identitymodel`) so it always moves together. See [MIGRATION.md](MIGRATION.md#065).

### Fixed
- `OidcExternalAuthProvider` — key-rotation recovery under IdentityModel 8.x. The new versions
  rate-limit `ConfigurationManager.RequestRefresh()` (a refresh-flooding guard), so the previous
  "force a metadata refresh and retry in the same request" no longer refetched and a freshly-rotated
  IdP signing key failed validation. The provider now fetches metadata + JWKS **directly** (one shot,
  bypassing the cached manager's cooldown) on a rotation signature-failure and retries once, so login
  recovers within the same request. Reaching that path requires a successful code exchange, so it is not
  an unauthenticated refresh vector.

## [0.6.4] - 2026-06-23

> **Upgrade straight to 0.6.4 — skip 0.6.3.** Because of a release-pipeline race (the 0.6.3
> publish job sat in the `nuget` approval gate while the fixes below merged, then published the
> *original* 0.6.3 commit and the later release runs self-skipped on the now-existing tag), the
> packages published as **0.6.3 do not contain the two fixes below** — they shipped FluentMigrator
> 7.2.0 and the deadlock-prone MySQL claim. 0.6.4 is 0.6.3 *as intended*. See [MIGRATION.md](MIGRATION.md#064).

### Fixed
- `Themia.Modules.Notifications.MySql` — the outbox claim deadlocked under concurrent drainers
  (`MySqlException: Deadlock found when trying to get lock`). InnoDB's default REPEATABLE READ takes
  gap/next-key locks on the `(status, next_attempt_at)` range scan that two claimers can deadlock on
  even with `FOR UPDATE SKIP LOCKED` (which only skips row locks). The claim transaction now runs at
  `READ COMMITTED` (no gap locks) with a bounded retry on error 1213. PostgreSQL and SQL Server are
  unaffected.

### Changed
- Bumped **FluentMigrator** and its PostgreSQL/MySQL/SQL Server runners `7.2.0 → 8.0.1`. Transparent
  to consumers using `ThemiaMigrations.Run(...)`; see [MIGRATION.md](MIGRATION.md#064) if you
  reference the FluentMigrator runner packages directly.
- Grouped Dependabot updates by package family (FluentMigrator, EF Core, Testcontainers, Quartz,
  Serilog, AWS SDK, Roslyn, ASP.NET Core, Microsoft.Extensions, xUnit) so a shared version moves in a
  single PR — prevents the split, mutually-conflicting per-package PRs that triggered this release race.

## [0.6.3] - 2026-06-23

### Added
- `Themia.Modules.Notifications` — tenant-aware notifications module over the `Themia.Notifications`
  core. A transactional outbox (`IOutboxStore`, staged in the caller's unit of work), a
  near-real-time background drainer (`OutboxDrainer` + `DrainSignal`) with per-engine atomic claim
  (PostgreSQL/MySQL `FOR UPDATE SKIP LOCKED`, SQL Server `READPAST/UPDLOCK` + `OUTPUT`), lease-based
  reclaim of crashed drainers, and exponential backoff → dead-letter (a `FormatException` is treated
  as a permanent failure). An `INotificationDispatcher` routes events to channels via per-tenant/user
  `NotificationPreference` (external channels enqueue; in-app writes directly). In-app notification
  store, per-tenant `TenantProviderConfig` resolver, EF Core + Dapper store peers over one
  FluentMigrator schema (PostgreSQL + MySQL + SQL Server), and an `AddThemiaNotificationsModule` DI
  extension. Targets `net10.0`.
- `Themia.Modules.Notifications.PostgreSql` / `Themia.Modules.Notifications.MySql` /
  `Themia.Modules.Notifications.SqlServer` — per-provider packages, each bundling the engine's
  Notifications SQL dialect (atomic outbox claim) and its database driver, registered via
  `AddThemiaNotificationsPostgreSql` / `AddThemiaNotificationsMySql` / `AddThemiaNotificationsSqlServer`.
  Target `net10.0`.

## [0.6.2] - 2026-06-22

### Added
- `Themia.Notifications` — neutral notification sending core. `NotificationMessage` model, channel
  senders (`IEmailSender` / `ISmsSender` / `IPushSender` seam), an `INotificationTemplateRenderer`
  (Handlebars.Net, used directly — no PuppeteerSharp/Chromium coupling), an SMTP email provider
  (`SmtpEmailSender` + `SmtpEmailOptions`, `System.Net.Mail`), an HTTP-SMS provider base
  (`HttpSmsSenderBase`), logger dev stubs, and an `AddThemiaNotifications` DI extension. Targets
  `net8.0;net10.0`. First slice of the Notifications module (the tenant-aware outbox/dispatcher follows
  in `Themia.Modules.Notifications`).

## [0.6.1] - 2026-06-22

### Added
- `Themia.Exceptional` — opt-in request-context capture (`ExceptionalOptions.CaptureRequestContext`)
  recording request headers, cookies, query, form, and server variables into a new nullable
  `RequestContext` column, with a configurable `Redactor` (default masks Authorization/Cookie/secret-named
  values; set to `null` to capture raw). A new forward-only migration adds the column across SQL Server /
  MySQL / PostgreSQL.
- `Themia.Exceptional.AspNetCore` — StackExchange.Exceptional-style dashboard: formatted stack trace,
  request-context sections (Server Variables / Headers / Cookies / Query / Form), relative time + summary
  header in the list, and protect/delete actions (POST behind a self-contained double-submit CSRF token).
  New options `EnableActions` and `ShowRequestContext`.

### Security
- Request-context capture is **off by default**. When enabled, the default `Redactor` masks the
  `Authorization`/`Cookie`/`Set-Cookie` headers and values whose key matches a secret-name pattern
  (`password`/`secret`/`token`/`apikey`/`session`). Other captured values — including cookies whose
  names don't match that pattern — are stored as-is; consumers needing stricter scrubbing supply a
  custom `Redactor`, and `Redactor = null` captures everything raw.

## [0.6.0] - 2026-06-21

### Added

- `Themia.Pdf` — neutral HTML→PDF rendering core. `IHtmlTemplateRenderer` (Handlebars.Net template
  merge) and `IPdfRenderer` (PuppeteerSharp headless-Chromium HTML→PDF) with a managed browser
  lifecycle, configurable Chromium provisioning (`ExecutablePath` / `DisableAutoDownload`), and an
  `AddThemiaPdf` DI extension. Targets `net8.0;net10.0`. First Phase-2 package. (ported from
  ezy-assets `ContractPdfService`)

## [0.5.6] - 2026-06-18

### Added

- **Typed `TenantId` construct/extract** in `Themia.Framework.Core` — `TenantId.From(int)`/`From(long)`/
  `From(Guid)` factories (canonical string encoding: invariant decimal for integers, lowercase `"D"`
  format for GUIDs) plus `AsInt32()`/`AsInt64()`/`AsGuid()` (throw `FormatException` on mismatch) and
  no-throw `TryAsInt32`/`TryAsInt64`/`TryAsGuid`. Lets int/long/guid apps adopt the string-keyed
  `TenantId` without hand-formatting at every call site, and centralizes the canonical encoding in one
  place so round-tripping can't drift.
- **`ClaimsTenantResolutionStrategy`** in `Themia.MultiTenancy` — resolves the tenant from an
  authenticated principal's claim (claim type via `MultiTenancyOptions.ClaimType`, default `tenant_id`),
  opt-in via `MultiTenancyBuilder.UseClaimsStrategy()`. Returns a fully **resolved** result carrying a
  minimal `TenantInfo` built from the claim, so resolution needs **no `ITenantStore` catalog** — the
  claim *is* the tenant. The natural fit for JWT apps (coord #0003).

## [0.5.5] - 2026-06-18

### Added

- **Consumer-exception → ProblemDetails mapping** in `Themia.AspNetCore` — `IProblemMappable` (implement on
  a consumer exception) and `services.AddThemiaProblemMapping<TException>(...)` (register a mapper for a
  type you don't own). Lets existing apps adopt `UseThemiaProblemDetails()` **without** replacing their
  exception types or rewriting throw sites. Both seams feed one write path and emit the same contract as
  the typed exceptions: `traceId`/`errorCode`/metadata extensions, a `ValidationProblemDetails` 400 `errors`
  dictionary (via `ValidationPropertyName`), and `Retry-After` header + `retryAfterSeconds` extension (via
  `RetryAfterSeconds`). Unblocks the ezy-assets middleware swap over its own ~585-throw-site taxonomy
  (coord #0002).

## [0.5.4] - 2026-06-18

### Added

- **`Themia.AspNetCore.Exceptions.RateLimitException`** → HTTP **429** via `ProblemDetailsMiddleware`,
  emitting a `Retry-After` header and a `retryAfterSeconds` problem extension. For rate-limit/cooldown
  paths (e.g. OTP resend) that previously had to fall back to a generic 400. `RetryAfterSeconds` is a
  domain value, so the exception stays HTTP-agnostic (the middleware owns the type→status map). Unblocks
  the ezy-assets Phase-1 middleware swap (coord #0001).

## [0.5.3] - 2026-06-17

### Added

- **`Themia.Storage`** (neutral core, `net8.0;net10.0`) — a framework-free object-storage
  abstraction: `IStorageProvider` (`Put`/`Get`/`Exists`/`Delete`/`GetPresignedUrl` over opaque
  keys) and a **Local filesystem backend** (`LocalStorageProvider`) with key sanitization
  (traversal/absolute keys rejected) and HMAC-SHA256 presigned URLs (`LocalUrlSigner`) that give
  the Local backend the same time-limited, tamper-evident URLs as S3/R2.
- **`Themia.Storage.S3`** (neutral, `net8.0;net10.0`) — an S3-compatible backend
  (`S3StorageProvider`, on `AWSSDK.S3`) that also drives **Cloudflare R2** and MinIO via a
  configured `ServiceUrl` + path-style addressing.
- **`Themia.Modules.Storage`** (net10.0) — tenant-aware object storage: `ITenantStorage` with
  **tenant key-prefix isolation**, DB-backed object metadata + **per-tenant quota** over the
  `storage.storage_objects` FluentMigrator schema (PostgreSQL + SQL Server), runnable on either
  data peer (**EF Core or Dapper**), DI-replaceable `IFileValidator` / `IFileScanner` seams, an
  `AddThemiaStorage().UseLocal()/UseS3()/UseR2()` builder, and an opt-in
  `MapThemiaStorageEndpoints` presigned-direct upload/download flow.
- **Presigned-upload reserve→complete flow.** `GetUploadUrlAsync` reserves a **pending** metadata row
  (quota-counted at the declared size but invisible to `Get`/`Exists` until confirmed); after the client
  uploads the bytes, `ITenantStorage.CompleteUploadAsync` (and the `POST /storage/complete` endpoint)
  stat the actually-stored object, reconcile the per-tenant quota to the **actual** size, and commit the
  reservation. Backed by a new nullable `storage_objects.committed_at` visibility marker and a provider
  `IStorageProvider.StatAsync` (metadata without a content stream).

### Security

- **Tenant isolation by construction** — every physical blob key is prefixed with the ambient
  tenant id, so one tenant can never address another's objects.
- **Upload validation** — size and content-type are validated (`IFileValidator`) before a write,
  with a DI-replaceable scan seam (`IFileScanner`) for malware checks.
- **Presigned-direct transfer** keeps object bytes off the application server (the client transfers
  straight to/from the backend), and secrets / credentials / presigned URLs are never logged.

## [0.5.2] - 2026-06-16

### Added

- **Pluggable external/OAuth login for Themia Identity.** New contracts in
  `Themia.Modules.Identity.Abstractions` (`IExternalAuthProvider`,
  `ExternalAuthRequest`/`ExternalIdentity`/`ExternalAuthResult`, `IExternalLoginService` +
  `ExternalLoginResult`, `IExternalAuthenticationFlow` +
  `ExternalLoginFlowResult`/`ExternalLoginOutcome`, `IExternalAuthenticationHooks`, the
  `ExternalLoginLink` entity, and `IUserService.CreateExternalUserAsync`) let any OAuth/OIDC
  provider plug into the same auth pipeline as password login.
- **`identity.external_logins` table** — tenant-scoped FluentMigrator migration
  (PostgreSQL + SQL Server) with a filtered-unique index per tenant + platform, plus EF Core and
  Dapper mappings. `ExternalLoginService` ships in the Identity core (`Themia.Modules.Identity`)
  and runs on both data peers: it resolves an existing link, otherwise auto-links by **verified**
  email, otherwise provisions a password-less user via the new password-less
  `CreateExternalUserAsync`.
- **`Themia.Modules.Identity.AspNetCore` external-auth stack** — a generic `OidcExternalAuthProvider`
  (server-side authorization-code→token exchange + id-token validation via JWKS/RS256 with
  `ConfigurationManager` auto-refresh, or HS256 with a channel secret), an
  `IExternalAuthProviderRegistry`, and a fluent
  `AddThemiaExternalAuth().AddGoogle(...).AddLine(...).AddProvider(...)/.AddOidc(...)` builder.
  `ExternalAuthenticationFlow` orchestrates the exchange, and `IExternalAuthenticationHooks`
  exposes DI-replaceable extension points.
- **Opt-in `MapIdentityExternalAuthEndpoints()`** — exposes
  `POST /auth/external/{provider}` (headless code-exchange) returning the **same**
  `AuthResponse { accessToken, expiresIn, refreshToken }` as login and rotating through
  `/auth/refresh`.
- **Reference providers: Google** (standard OIDC) and **LINE** (OIDC-ish, HS256 channel secret).
  Facebook / Microsoft / Telegram are deferred additive providers.

### Security

- **Auto-link only on a verified provider email.** A link is created automatically only when the
  provider asserts a verified email; an unverified email never links and is never persisted.
- **Server-side code exchange** keeps the client secret off the wire (never exposed or logged),
  with the id-token issuer / audience / signature / expiry all validated and the PKCE
  `code_verifier` forwarded. Provider failures return a **uniform 401** (404 only for an unknown
  provider). The flow is headless: the client owns `state` (CSRF).
- **Token-bound nonce validation.** If the id-token carries a `nonce` claim the client must supply
  the matching value (and vice-versa); the check is skipped only when neither side asserts a nonce.
  This closes the bypass where omitting the nonce field would skip validation on a token that
  actually carries one.
- **Verified-email auto-link is gated on account state.** A deactivated or locked-out account is
  never auto-linked to a new external credential (it is returned un-linked for the flow's
  active/lockout gate to block), so a later re-activation cannot silently inherit an external login.
- **Concurrent first-login is race-safe.** A lost race on the `(provider, subject)` link index *or*
  on the new user's unique name/email index is retried (bounded): the next pass resolves the
  existing link, auto-links by verified email, or derives a fresh user name — instead of surfacing
  a 500. The provisioning of a new user and its link remains atomic in one transaction.
- **Bounded discovery/JWKS connection age.** The OIDC discovery/JWKS client held by the singleton
  provider uses a `PooledConnectionLifetime` so DNS/endpoint changes are picked up despite the
  long-lived `ConfigurationManager`.
- **Refresh honours account state.** `IAuthenticationFlow.RefreshAsync` now rejects a refresh whose
  user is deactivated or locked out (returning `Invalid`), so deactivation/lockout takes effect
  immediately instead of only when the refresh token expires — closing a bypass that also predated
  the external-login slice.
- **Platform external login is repeatable.** The external-link lookup gained a platform (global,
  `tenant_id IS NULL`) fallback gated on `AllowPlatformLogin`, mirroring `FindByEmailAsync`. Without
  it, a platform user's second external login could fail on a data layer that hides global rows from
  tenant scopes (Dapper's default `IncludeGlobalRecordsForTenants=false`).
- **Failed transactions no longer leak EF change-tracker state.** `EfUnitOfWork.ExecuteInTransactionAsync`
  clears the change tracker when the work/save throws, so a retry on the same scoped `DbContext`
  (e.g. the race-retry loop) does not re-attempt the rolled-back writes.
- **`email_verified` accepts a string boolean.** Some OIDC providers serialize the claim as `"true"`
  rather than a JSON boolean; both forms are now honoured.

## [0.5.1] - 2026-06-15

### Added

- **`Themia.Modules.Identity.AspNetCore`** (net10.0) — JWT access-token issuance, revocable
  rotating refresh tokens with token-family reuse-detection (family invalidated on token reuse),
  JwtBearer validation scheme (`AddThemiaJwtBearer`), `AddThemiaIdentityAspNetCore` DI entry
  point, opt-in `MapIdentityAuthEndpoints()` (login / refresh / logout), and a DI-replaceable
  `IAuthenticationFlow` + `IAuthenticationHooks` extension pair.
- **`identity.refresh_tokens` table** — FluentMigrator migration (PostgreSQL + SQL Server);
  `RefreshTokenService` ships in the Identity core (`Themia.Modules.Identity`) and runs on both
  EF Core and Dapper data peers.
- **`IdentityModuleOptions.RefreshTokenLifetime`** — configurable refresh-token TTL (default 14 days).

### Changed

- **FluentMigrator upgraded to 7.2.0** (from 6.x). FM7 renamed the PostgreSQL generator id, so
  `IfDatabase("postgres")` matched nothing and the Postgres branch silently no-opped while
  `VersionInfo` recorded the migration as applied; all migrations now route via
  `IfDatabase("postgresql")`. Resulting schema is unchanged.
- **`Themia.Data.Migrations` references only the provider runners Themia supports** —
  `FluentMigrator.Runner.Postgres` / `.MySql` / `.SqlServer` — instead of the
  `FluentMigrator.Runner` meta-package, dropping seven unused providers (Db2, Oracle, Hana,
  Snowflake, Redshift, Firebird, SQLite) from the dependency graph.
- Routine dependency updates (TimeProvider.Testing, SqlKata, StackExchange.Redis, test SDKs).

### Fixed

- **FluentMigrator-dependent test projects no longer report "inconclusive" in Rider/ReSharper on
  Apple Silicon.** The `FluentMigrator.Runner` meta-package dragged in the x64-only
  `IBM.Data.Db2.dll`, whose PE machine header (AMD64) made the IDE force an x64 test host; with no
  x64 .NET installed on an arm64 machine the run aborted. Trimming to the used providers removes the
  x64 assembly so tests run natively (arm64). Headless `dotnet test` was unaffected.

### Security

- Login is **anti-enumeration, uniform-401**: an argon2id dummy hash is computed on not-found /
  inactive / locked-out paths to equalize timing across all failure modes, preventing username
  enumeration via response-time side-channel.

## [0.5.0] - 2026-06-14

### Added
- `Themia.Modules.Identity.Abstractions` and `Themia.Modules.Identity`: tenant-aware Identity core —
  user/role/claim store with full account lifecycle (lockout, email/phone confirmation + password-reset
  tokens, a 2FA flag), argon2id password hashing, the `ICurrentUser` principal + `ClaimsPrincipalFactory`,
  and ASP.NET Core authorization integration. Runs on either data peer (EF Core or Dapper) over a
  FluentMigrator schema (PostgreSQL + SQL Server). Platform (cross-tenant) users are modeled as global
  records (`tenant_id IS NULL`). First slice of the full Identity provider (JWT → 0.5.1, external/LINE
  login → 0.5.2).

## 0.4.10 — 2026-06-13

### Fixed

- **`ProblemDetailsMiddleware` no longer turns a client-aborted request into a 500.** When the client
  disconnects mid-request, the resulting `OperationCanceledException` was caught by the generic handler,
  logged at `Error`, and written as a 500 to a dead connection. It is now treated as cancellation flow: when
  `HttpContext.RequestAborted` is signalled, the middleware logs at `Debug` and lets the cancellation
  propagate without writing a response (checked ahead of the response-already-started path, so a client abort
  is never logged as an error). A genuine (non-client-abort) `OperationCanceledException` still takes the
  generic 500 path.

## 0.4.9 — 2026-06-13

### Added

- **Tenant-isolation analyzers (THEMIA103/104).** `Themia.Analyzers` now ships two build-time rules
  (category `Themia.Isolation`, Warning) closing DECISION #6's by-construction gap: **THEMIA103** flags
  raw Dapper connection access (`IDapperConnectionContext.GetOpenConnectionAsync`), steering to
  `ITenantQueryFactory.For<T>()`; **THEMIA104** flags `DbSet<T>.Find/FindAsync`, which bypasses
  `ThemiaDbContext`'s tenant post-check for already-tracked entities, steering to `DbContext.FindAsync<T>()`
  / `IReadRepository.GetByIdAsync()`. Both stay silent inside the `Themia.Framework.Data.*` assemblies and
  fire everywhere else. Deliberate bypasses use standard suppression (`#pragma`/`[SuppressMessage]`).

### Changed

- **`Themia.Analyzers` now flows to consumers of the `Themia.Framework.Data.*` packages.** Adopters of a
  Themia data package will see Themia analyzer warnings — the new isolation gates plus the pre-existing
  THEMIA101 (catch-log-rethrow) / THEMIA102 (sync-over-async) hygiene rules. Configure severity or suppress
  per `.editorconfig`. See [MIGRATION.md](MIGRATION.md).

## 0.4.8 — 2026-06-12

### Added

- **Persistent Quartz (AdoJobStore), default-on.** `Themia.Modules.Scheduling` now registers and starts a
  Quartz.NET scheduler backed by AdoJobStore — the `qrtz_*` schema is created in a dedicated `quartz` schema by a
  FluentMigrator migration (PostgreSQL + SQL Server, run through `ThemiaMigrations.Run`), with
  `UseSystemTextJsonSerializer()` (no Newtonsoft) and `UseProperties = true`. Scheduled jobs now survive a
  restart. Set `SchedulingModuleOptions.UsePersistentStore = false` to keep a host-supplied scheduler.

### Fixed

- **Persistent Quartz on case-sensitive SQL Server collations.** The SQL Server AdoJobStore `TablePrefix` and
  the migration's existence guard now use the uppercase `QRTZ_*` table names that the verbatim Quartz DDL
  creates, instead of lowercase. A case-insensitive collation (the default) masked the mismatch, but under a
  case-sensitive collation Quartz could not resolve the tables and the cutover replay re-ran the DDL. Covered by
  a case-sensitive-collation integration test.

## 0.4.7 — 2026-06-12

### Changed

- **`Themia.Modules.Scheduling`** now creates its schema through FluentMigrator (the shared
  `Themia.Data.Migrations` runner, DECISION #6) instead of EF Core migrations, and is **provider-agnostic
  over PostgreSQL and SQL Server** (was PostgreSQL-only). The module selects the EF provider and migration
  engine from the app's registered `IDatabaseProvider`, so it now **requires** one
  (`AddThemiaPostgres`/`AddThemiaSqlServer`). Execution history remains process-wide (the `Default`
  connection, never tenant-routed).
- `AddThemiaDbContext` (and thus `AddThemiaPostgres`/`AddThemiaSqlServer`) now registers the active
  `IDatabaseProvider` in DI so modules can resolve the app's database engine.

### Removed

- The scheduling module's EF Core migration artifacts and design-time `DbContext` factory — its schema is
  FluentMigrator-owned.

## 0.4.6 — 2026-06-12

Foundation slice of the FluentMigrator-authority program (DECISION #6): the FluentMigrator runner that
was triplicated inside the three `Themia.Exceptional.*` provider packages becomes one neutral package
that any neutral core or framework module can hand its migrations to.

### Added

- **`Themia.Data.Migrations`** — a neutral (`net8.0;net10.0`) shared FluentMigrator runner.
  `ThemiaMigrations.Run(MigrationEngine engine, string connectionString, params Assembly[] migrationAssemblies)`
  selects the engine's processor (`Postgres`/`MySql`/`SqlServer`), scans the supplied assemblies, and
  applies pending migrations (`MigrateUp`), wrapping failures in an `InvalidOperationException` that names
  the engine.

### Changed

- The `Themia.Exceptional.*` packages now apply their schema migration through the shared runner instead
  of each carrying an identical inline runner. The adopter-facing `AddThemiaExceptional{Postgres,MySql,SqlServer}`
  API is unchanged. The provider-author extension `AddThemiaExceptionalProvider` now takes a
  `MigrationEngine` instead of an `Action<IMigrationRunnerBuilder>` + display-name pair.

## 0.4.5 — 2026-06-11

SQL Server provider for the EF Core data layer — the EF side starts catching up with the three-engine
Dapper set (DECISION #6: EF and Dapper are selectable first-class peers). The EF layer is restructured
into per-engine provider packages, and framework-column naming is now explicit so adopters keep
idiomatic casing for their own tables.

### Added

- **`Themia.Framework.Data.EFCore.SqlServer`** — SQL Server EF Core provider (`AddThemiaSqlServer`,
  `SqlServerDatabaseProvider`) with DB-per-tenant connection routing, plus a full integration suite
  (Testcontainers mssql 2022) covering tenant isolation, audit, soft delete, `rowversion`
  concurrency, and the naming split.
- **`Themia.Framework.Data.EFCore.PostgreSql`** — the PostgreSQL provider, extracted from the core
  package into its own per-engine package (mirrors the Dapper layer topology).
- `DatabaseConnectionStringResolver` — shared tenant-or-default connection-string resolution in core,
  used by both providers so the resolution rule cannot drift between engines.

### Changed

- **(breaking)** `Themia.Framework.Data.EFCore` is now **provider-agnostic**: `AddThemiaPostgres`
  moved to `Themia.Framework.Data.EFCore.PostgreSql`, and core no longer references Npgsql or
  EFCore.NamingConventions.
- **(breaking)** Framework columns (entity key + audit/tenant/soft-delete/concurrency) are mapped to
  explicit snake_case in `ThemiaDbContext`; the providers no longer force a global naming convention,
  so adopter columns follow the EF provider default (PascalCase on SQL Server). Whole-model
  snake_case remains available via the standard EF mechanism: reference `EFCore.NamingConventions`
  and pass `configureOptions: o => o.UseSnakeCaseNamingConvention()` — the provider packages no
  longer carry that dependency.

### Removed

- **(breaking)** `AddThemiaDbContextWithProvider` (string-name provider factory) — call the
  per-engine `AddThemiaPostgres` / `AddThemiaSqlServer` entry points instead.

### Fixed

- **Cross-tenant leak via `DbSet.Find`/`FindAsync`** — EF's pre-compiled entity-finder query baked
  the first-seen ambient tenant into the cached by-PK plan (the runtime filter was rooted at a static
  property). The filter is now rooted at the context instance, so every path — including `Find` —
  parameterizes the tenant per execution. Pre-existing since the EF data layer shipped; exposed by
  the new SQL Server integration suite. Analysis:
  `docs/2026-06-11-efcore-sqlserver-find-isolation-issue.md`.

## 0.4.4 — 2026-06-10

SQL Server engine for the Dapper data layer — completes the three-engine set (PostgreSQL, MySQL, SQL Server),
so a Dapper-first app on SQL Server gets the framework's tenant isolation, audit, soft-delete, and
unit-of-work guarantees.

### Added

- **`Themia.Framework.Data.Dapper.SqlServer`** — SQL Server engine for the Dapper data layer
  (`Microsoft.Data.SqlClient` + SqlKata `SqlServerCompiler`). Completes the three-engine set
  (PostgreSQL, MySQL, SQL Server). Native `uniqueidentifier`↔`Guid` mapping, `OFFSET/FETCH` paging
  (`UseLegacyPagination = false`), `datetime2(7)` audit timestamps via a `DbType.DateTime2`
  `DateTimeOffset` handler, and store-generated `INT IDENTITY(1,1)` keys via native `scope_identity()`.
  Conformance is Dapper-only (the EF data layer remains PostgreSQL-only), proven against a real SQL Server
  container.

### Changed

- The per-engine `DateTimeOffset` type-handler registration is now a single shared mechanism in the Dapper core
  (`DapperConfiguration.ConfigureEngine`). Because Dapper's type-handler registry is process-global, registering
  two engines in one process now **fails fast** with a clear error instead of silently corrupting one engine's
  timestamp writes — a single Themia Dapper engine per process was always the contract; it is now enforced.

## 0.4.3 — 2026-06-10

MySQL engine for the Dapper data layer — the sibling to the PostgreSQL engine, so a Dapper-first app on MySQL
gets the framework's tenant isolation, audit, soft-delete, and unit-of-work guarantees.

### Added

- **`Themia.Framework.Data.Dapper.MySql`** — MySQL engine for the Dapper data layer (`MySqlConnector` +
  SqlKata `MySqlCompiler`), registered via `AddThemiaDapperMySql`. Honours the full shared data-layer contract
  (tenant isolation, audit, soft-delete, unit of work) — proven by the conformance suite against `mysql:8.4`.
  `GuidFormat=Char36` is enforced for Guid keys; store-generated keys use `LAST_INSERT_ID()` (AUTO_INCREMENT
  integers; store-generated UUID remains PostgreSQL-only).

## 0.4.2 — 2026-06-10

Write-path tenant isolation: both data layers now reject a cross-tenant `UPDATE`/`DELETE`, closing the EF
write gap where a detached entity carrying another tenant's primary key could mutate that tenant's row.

### Changed

- **Write-path tenant isolation is now enforced on both data layers.** A tenant-scoped `UPDATE`/`DELETE`
  that targets a row outside the current tenant throws `ConcurrencyException` (EF verifies the stored row's
  tenant by primary key; Dapper scopes the SQL predicate). `IDataFilterScope.BypassTenantFilter()` now also
  applies to writes as an admin/migration escape hatch on both layers.

## 0.4.1 — 2026-06-09

A Dapper (+ SqlKata) data layer as a first-class sibling to EF Core, behind a shared,
provider-agnostic abstraction (specifications, repositories, unit of work) with multi-tenant
isolation, audit, soft-delete, and a dual-provider conformance suite — PostgreSQL first.

### Added

- **`Themia.Framework.Data.Abstractions`** — provider-agnostic data-access contracts: `ISpecification<T>`
  (+ `Specification<T>` base and And/Or/Not combinators), `IReadRepository`/`IRepository`, `IUnitOfWork`/
  `ITransactionScope`, `IDataFilterScope` (tenant-filter bypass), `ICurrentUserAccessor`, `PagedResult<T>`,
  and a `ConcurrencyException` raised when a single-entity update/delete affects no rows (a lost write —
  missing row, concurrent delete, or outside the tenant scope) on both the Dapper and EF Core layers.
- **`Themia.Framework.Data.Dapper`** + **`Themia.Framework.Data.Dapper.PostgreSql`** — a Dapper + SqlKata
  data layer implementing the shared contracts with multi-tenant isolation, audit, soft-delete, and a
  deferred-write unit of work, plus a tenant-seeded native-SqlKata path (`ITenantQueryFactory`) and an
  `ISpecification<T>`→SqlKata translator. PostgreSQL via `AddThemiaDapperPostgres`. (PostgreSQL this release;
  MySQL/SQL Server are planned 0.4.x follow-ups.)
- **`Themia.Framework.Data.EFCore`** now also implements the shared contracts via
  `AddThemiaDataRepositories<TContext>()` (`EfReadRepository`/`EfRepository`/`EfUnitOfWork`), so application
  code written against the abstraction runs on either the EF Core or the Dapper data layer. A Testcontainers
  conformance suite runs the same behavioural tests against both providers.

### Changed

- **`Themia.Framework.Data.EFCore` (PostgreSQL): automatic transient-fault retry (`EnableRetryOnFailure`)
  is no longer enabled.** A retrying EF execution strategy is incompatible with the user-initiated
  transactions now exposed via `IUnitOfWork.BeginTransactionAsync`. Hosts needing retry and not using
  manual transactions can re-enable it through the `configureOptions` delegate of `AddThemiaPostgres`.
- The Dapper data layer auto-stamps the ambient tenant on insert (matching the EF layer); the EF
  repository adapter now does the same. Inserting a global (null-tenant) row through the repository is
  therefore not possible while a tenant is ambient — seed global/shared rows via migrations or direct
  `DbSet`/raw SQL.

## 0.4.0 — 2026-06-07

Scheduling capability: a framework-neutral Quartz dashboard core + an EF-backed scheduling module.

### Added

- `Themia.Quartz` (`net8.0;net10.0`) — framework-neutral Quartz.NET dashboard, vendored from SilkierQuartz
  (re-namespaced `Themia.Quartz.Dashboard`) for full ownership. Provides `AddThemiaQuartz(...)` +
  `MapThemiaQuartz()`/`UseThemiaQuartz()`, a host-supplied `ThemiaQuartzOptions.Authorize` delegate
  (**deny-all when unset** — the cookie/login `AuthenticateController` is dropped; the host owns auth),
  the vendored `RecentHistory` execution-history contract (`IExecutionHistoryStore`) + an in-memory store,
  and a DI→scheduler-context store bridge. Validated end-to-end (routes, 403-when-denied, embedded
  dashboard content) on net8 + net10.
- `Themia.Modules.Scheduling` (`net10.0`) — `SchedulingModule : ThemiaModuleBase` wiring the dashboard +
  an **EF-backed global execution-history store** (`EfExecutionHistoryStore`, **not tenant-scoped** — the
  scheduler is process-wide admin infrastructure). Schema is created via an EF Core migration on
  `InitializeAsync`. The store creates a short-lived `DbContext` per operation via `IDbContextFactory`,
  so it is safe under concurrent Quartz job listener callbacks.

### Notes

- **`Themia.Quartz` is now System.Text.Json-only** — `Newtonsoft.Json`, `JsonSubTypes`, and
  `Microsoft.AspNetCore.Mvc.NewtonsoftJson` have been removed. The vendored SilkierQuartz dashboard's JSON
  layer was migrated to STJ: a polymorphic type-handler converter (replaces `JsonSubTypes`) + a
  `System.Type` converter, with a wire-format regression suite pinning the exact JSON output on both
  `net8.0` and `net10.0`.
- **`Themia.Modules.Scheduling` is PostgreSQL-only in this phase** (hardcoded Npgsql provider + `scheduling`
  schema); generalizing to the framework's multi-provider story is deferred. Its dashboard `Authorize`
  default is authenticated-only — hosts should tighten it to an admin check (the dashboard is platform-admin).

## 0.3.2 — 2026-06-07

P3 hardening: SqlServer write precision, de-duplicated provider registration, a shared engine
conformance test suite, and real DI-generator incrementality (clearing two 0.3.1 known limitations).

### Fixed

- `Themia.Exceptional` — write-side temporal parameters (INSERT/rollup/soft-delete/purge) are now bound
  with the provider's correct `DbType` via dialect-owned write parameters, so **SqlServer `datetime2`
  columns keep sub-3.33 ms precision** (Dapper's default `DateTime` inference rounded to legacy `datetime`,
  silently truncating on write). Postgres/MySQL behavior is unchanged.
- `Themia.SourceGenerator` — the DI registration generator is now **genuinely incremental**: all semantic
  analysis runs in the `transform` and the pipeline carries only equatable, compilation-free data (a
  registration record + a replayable `DiagnosticInfo`), so the output node caches across unrelated edits
  instead of re-running. Generated output and every diagnostic are byte-identical. Resolves the 0.3.1
  known limitation.

### Changed

- `Themia.Exceptional` — provider packages (`PostgreSql`/`MySql`/`SqlServer`) now delegate to a shared
  neutral `AddThemiaExceptionalProvider` helper; each provider package retains only its four deltas
  (method name, dialect, FluentMigrator runner call, display name). No behavior change.

### Tests

- `Themia.Exceptional` — the three engine integration suites now share an `ExceptionStoreConformanceTests`
  base (one `IExceptionStore` contract asserted identically on PostgreSQL/MySQL/SQL Server), replacing
  ~640 lines of triplicated tests; engine-specific tests (e.g. SqlServer `datetime2` precision) stay local.

## 0.3.1 — 2026-06-06

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

