# Migration Guide

Upgrade notes and breaking-change guidance between **Themia** versions. Every
**(breaking)** entry in [CHANGELOG.md](CHANGELOG.md) has a matching section here
with the *why* and concrete upgrade steps.

## How to read this guide

- Sections are ordered **newest first**, headed by the version that introduced the change.
- Each entry states: **What changed**, **Why**, and **How to upgrade** (before → after).
- Non-breaking changes are *not* listed here — see the CHANGELOG.

## Unreleased

### `Themia.Modules.Identity` splits into engine packages

**What changed:** the identity store's engine-specific pieces move into
`Themia.Modules.Identity.Dapper` and `Themia.Modules.Identity.EFCore`. The core keeps every service and
carries no data peer, no database driver and no migration runner.

**Why:** the core depended on **both** `Themia.Framework.Data.Dapper` and `Themia.Framework.Data.EFCore`,
so every adopter shipped an engine they do not use — and an application with no EF Core at all could not
adopt Identity without acquiring one (coord #0058).

**How to upgrade — pick exactly one engine package:**

```xml
<PackageVersion Include="Themia.Modules.Identity" Version="0.13.0" />
<PackageVersion Include="Themia.Modules.Identity.Dapper" Version="0.13.0" />   <!-- or .EFCore -->
```

```csharp
  services.AddThemiaDapperPostgres(configuration);   // peer first, unchanged
- services.AddThemiaIdentityServices(o => o.AllowPlatformLogin = true);
+ services.AddThemiaIdentityDapper(o => o.AllowPlatformLogin = true);
```

EF Core adopters call `AddThemiaIdentityEFCore` instead, and continue to call `ApplyThemiaIdentity` from
`OnModelCreating` — the type name and namespace are unchanged, only the package that delivers it.

**`AddThemiaIdentityServices` is now a compile error**, renamed to `AddThemiaIdentityCore`. It used to scan
the service collection for an `EntityMappingRegistry` and contribute the identity mappings to whatever it
found. That inferred the Dapper path rather than being told it, and inferred wrong — silently — whenever
the two registrations ran in the other order: no exception, no log, identity mappings simply never applied,
until a query came back against unqualified `users` instead of `identity.users`.

Leaving the old name callable would have been worse than removing it. Its signature is unchanged, so an
existing Dapper bootstrap — even one that already calls the peer first, in the correct order — would have
recompiled with zero errors and zero warnings and then lost the `identity.` qualification: an auth outage
on first login, with nothing pointing at the upgrade. `[Obsolete(error: true)]` puts the failure at build
time and names the replacement in the message. `AddThemiaIdentityCore` still registers the engine-agnostic
services; call it directly only if you are supplying your own `IRepository` implementations.

**If you use the module:** `new IdentityModule(MigrationEngine.Postgres)` becomes
`new IdentityDapperModule(MigrationEngine.Postgres)` or `new IdentityEFCoreModule(...)`. The
`MigrationEngine` argument is the *database* and is orthogonal to the peer — it stays. **The rename is not
the whole change for this path — two more things move:**

- **⚠ Configure the module AFTER the Dapper peer registration.** `IdentityDapperModule.ConfigureServices`
  contributes the identity mappings and now *throws* when the registry does not exist yet, so a host whose
  module loop runs before `AddThemiaDapperPostgres(configuration)` fails to start. `IdentityModule`
  tolerated either order — wrongly, by leaving the tables unmapped — so a host that happened to be in the
  bad order was working by accident and will now stop at boot with the ordering named. Move the peer
  registration above the module loop.
- **⚠ The module identifier changed, not just the type.** `ModuleDescriptor.Name` goes from
  `"Themia.Identity"` to `"Themia.Identity.Dapper"` / `"Themia.Identity.EFCore"`. That string is the key
  `ModuleDescriptor.Dependencies` resolves against, and hosts commonly key module enablement off it too —
  a `modules` table row, a `Modules:Themia.Identity:Enabled` config entry. A stored `"Themia.Identity"`
  matches nothing after the upgrade, so the module reads as disabled, `ConfigureServices` never runs, and
  every authenticated request fails on an unresolved `IUserService`. Update the stored keys with the type.

**EF Core: the model check now runs at startup.** `AddThemiaIdentityEFCore` registers a hosted service that
verifies the resolved `ThemiaDbContext` actually maps `User` to `identity.users`. If you forgot
`modelBuilder.ApplyThemiaIdentity()`, or applied it to a different context than the one passed to
`AddThemiaDataRepositories<TContext>`, the host now fails to start with that sentence instead of succeeding
into a first user operation that queries a table EF Core has never heard of.

**The core applies no schema on its own.** It carries the FluentMigrator migration classes but no runner —
running them needs a driver for every engine, and the core stays driver-free. Both engine modules run them
on startup. If you reference only the core, pass
`Themia.Modules.Identity.Migrations.IdentityMigrations.Assembly` to a runner of your own.

**No compatibility shim ships** for the two moved members. Both consumers confirmed zero call sites, and a
forwarding type nobody uses is a spare compatibility surface — it lets a caller keep an old assumption
compiling, which is the failure mode `NotificationResult.Success()` demonstrated in 0.12.0.

### Dapper mapping contribution is one mechanism across all modules

**What changed:** `Themia.Framework.Data.Dapper` gains `ContributeDapperMappings` /
`RequireDapperMappings`, and Identity, Storage, Notifications and Messaging all use them. Two behaviours
change for adopters:

- **`AddThemiaStorage` and `AddThemiaNotificationsModule` now throw** when a Dapper peer is registered but
  its `EntityMappingRegistry` is not. They used to return quietly, which is never a legitimate state — it
  means the module was registered before the peer, and the tables stay unmapped until a query fails.
- **`AddThemiaDapperCore` called twice no longer registers two registries.** The second instance won
  resolution while every mapping the modules had contributed sat on the first, so every module-mapped table
  silently fell back to its convention name.

**Why:** four modules had hand-rolled the same service-collection scan and the copies drifted into three
different behaviours for one adopter mistake. Registering the peer after the modules produced a hard
failure from Identity and silently unmapped `storage`/`notifications` tables in the same startup.

**How to upgrade:** if your app starts and nothing throws, nothing to do. If one of these now throws, move
`AddThemiaDapper{Postgres|MySql|SqlServer}(configuration)` above the module registrations — the exception
names the method to move. A genuine EF Core adopter (no registry, no `IDapperConnectionContext`) is
unaffected.

## 0.12.2

### `Themia.Modules.Identity`: multi-identifier login, and a phone number that finally works

**What changed:**

- `IAuthenticationFlow.LoginAsync(string userName, …)` is now `LoginAsync(string identifier, …)` and
  accepts a username, a **confirmed** email, or a **confirmed** phone number.
- `LoginFailureReason` gained `AmbiguousIdentifier`.
- `IUserService` gained `FindByPhoneAsync`, `SetPhoneNumberAsync` and `ConfirmPhoneNumberAsync`.
- `UserService`'s constructor gained an `IPhoneNumberNormalizer` parameter.
- New migration `202608050001` adds `identity.users.normalized_phone_number` and two filtered unique
  indexes.

**Why:** the login path resolved by username only, so a user typing their email got a 401 that could not
be told apart from a wrong password — the production incident behind coord #0054. Underneath it,
`PhoneNumber` had shipped with no normalized form, no uniqueness, no index and nothing that ever wrote
it: storable and unusable.

**How to upgrade:**

- **Positional callers of `LoginAsync` need no change.** Only a named argument (`userName:`) breaks.
- **Handle `LoginFailureReason.AmbiguousIdentifier`** if you `switch` exhaustively. It means one string
  matched two different users, so somebody cannot log in and two accounts overlap in a way per-column
  uniqueness cannot prevent — worth alerting on. Do not surface it to the caller; that would confirm a
  second account holds that string.
- **If you implement `IUserService`**, implement the three new members. If you construct `UserService`
  directly, pass an `IPhoneNumberNormalizer` — `FormattingOnlyPhoneNumberNormalizer` is the default and
  `AddThemiaIdentity` registers it with `TryAdd`.
- **Nothing changes for username-only logins.** Email and phone match only when confirmed, and no
  existing row has a normalized phone number until you set one.

**⚠ THE MIGRATION FAILS IF TWO USERS ALREADY SHARE A PHONE NUMBER.** That is deliberate — creating the
unique index is the first moment the database can tell you two accounts claim one number, and permitting
it would leave exactly the ambiguity that makes phone login unsafe. Find them first:

```sql
-- per tenant
SELECT tenant_id, phone_number, COUNT(*) FROM identity.users
WHERE phone_number IS NOT NULL AND tenant_id IS NOT NULL
GROUP BY tenant_id, phone_number HAVING COUNT(*) > 1;

-- platform scope
SELECT phone_number, COUNT(*) FROM identity.users
WHERE phone_number IS NOT NULL AND tenant_id IS NULL
GROUP BY phone_number HAVING COUNT(*) > 1;
```

Note this checks the raw column; the index is built on the *normalized* form, so two rows that differ
only in formatting will collide even though this query says they are distinct.

**Existing phone numbers are deliberately not backfilled.** They keep a null normalized form and are not
login identifiers until re-set through `SetPhoneNumberAsync`. Normalizing them in the migration is
impossible — the rule lives in your `IPhoneNumberNormalizer` and cannot be reached from there — and
applying some other rule would write values the running application disagrees with: a row findable by the
index but not by the code. They were unusable before this migration anyway.

## 0.12.1

### `IChallengeDialect`: five statements became methods taking a shape

**What changed:** `SelectLiveByScopeSql`, `SelectMostRecentByScopeSql` and `InvalidateLiveForScopeSql`
are now methods taking a `ChallengeTenancy`; `IncrementWindowSql` and `DecrementWindowSql` are methods
taking a `RateWindowBucket`. Two new public enums come with them.

**Why:** each statement used to compare nullable columns with a null-safe form so that one SQL text
covered both a bound parameter and a `NULL` one. Every such form — PostgreSQL's `IS NOT DISTINCT FROM`,
MySQL's `<=>`, SQL Server's `(a = b OR (a IS NULL AND b IS NULL))` — is non-sargable, so none of the
indexes the schema creates could be seeked. On PostgreSQL 16 over 200 000 rows the increment `UPDATE`
that `IssueAsync` runs two or three times per call was a sequential scan at 16.2 ms; the shape-specific
predicate is an index scan at 0.042 ms. The OR-guard does not recover it even with literals, so the SQL
text itself had to change.

**How to upgrade:**

- **If you only reference the shipped engine packages** (`Themia.Challenges.PostgreSql` / `.MySql` /
  `.SqlServer`) — nothing to do. `IChallengeService` is unchanged.
- **If you implement `IChallengeDialect` yourself** — convert those five members from properties to
  methods and emit a shape-specific predicate for each case:

  ```csharp
  // before
  public string DecrementWindowSql => """
      UPDATE challenge_rate_windows SET count = GREATEST(count - 1, 0)
      WHERE tenant_id IS NOT DISTINCT FROM @TenantId AND "key" = @Key
        AND purpose IS NOT DISTINCT FROM @Purpose AND window_start = @WindowStart;
      """;

  // after
  public string DecrementWindowSql(RateWindowBucket bucket)
  {
      var (tenant, purpose) = bucket switch
      {
          RateWindowBucket.TenantAndPurpose => ("tenant_id = @TenantId", "purpose = @Purpose"),
          RateWindowBucket.TenantAllPurposes => ("tenant_id = @TenantId", "purpose IS NULL"),
          RateWindowBucket.PlatformAndPurpose => ("tenant_id IS NULL", "purpose = @Purpose"),
          RateWindowBucket.PlatformAllPurposes => ("tenant_id IS NULL", "purpose IS NULL"),
          _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
      };

      return $"""
          UPDATE challenge_rate_windows SET count = GREATEST(count - 1, 0)
          WHERE {tenant} AND "key" = @Key AND {purpose} AND window_start = @WindowStart;
          """;
  }
  ```

  **A shape that means `NULL` must say `IS NULL` and must never compare the column to its parameter.**
  `column = @Param` against a `NULL` parameter matches zero rows in silence — no error, every
  platform-level challenge and every per-key ceiling row simply invisible, and the per-key ceiling is
  what bounds an SMS bill. `Themia.Challenges.Tests.ChallengeDialectContractTests` enforces both halves
  of that if you run it against your dialect.

### `Themia.Challenges`: `VerifyAsync` is now rate-limited

**What changed:** a new `ChallengeOptions.VerifyWindow` (default 20 per 15 minutes) bounds verification
attempts per key. A refused call returns the new `ChallengeVerifyOutcome.RateLimited`.

**Why:** `MaxAttempts` lives on a challenge row, so it bounded nothing when no challenge was live —
verification traffic against a key with nothing outstanding was unbounded and uncounted.

**How to upgrade:**

- **Handle the new outcome.** If you `switch` exhaustively over `ChallengeVerifyOutcome`, the compiler
  will tell you where. Treat it as a failure the caller should retry later; on an anonymous endpoint it
  must be as indistinguishable from the other failures as they are from each other.
- **Tune it if 20 per 15 minutes is wrong for you.** It cannot be disabled, only tuned — an off switch
  is how a rate limit ships disabled by accident.
- A rate-limited call does **not** count against `MaxAttempts`, so it cannot be used to burn someone
  else's live challenge.

## 0.12.0

### MariaDB is no longer a supported engine

**What changed:** the package descriptions, XML docs and migration error messages that said
"MySQL/MariaDB" now say MySQL. `README.md` and `docs/themia-architecture-overview.md` state the
supported set as SQL Server, **MySQL 8.0.13+**, and PostgreSQL. No code path changed, and nothing
changes for MySQL adopters.

**Why:** the claim was never true for every package that carried it. The MySQL leg of the shared schema
uses **functional key parts** (`CREATE UNIQUE INDEX ... ((expr))`, MySQL 8.0.13+) to emulate the
partial and filtered unique indexes PostgreSQL and SQL Server have natively — see
`Themia.Modules.Pdf.Migrations.PdfTemplateSchemaMigration` and
`Themia.Challenges.Migrations.ChallengeSchemaMigration`. MariaDB has no equivalent syntax at any
version, so those migrations fail to parse and the module cannot install. The claim was inherited
across specs and had never been tested on any package carrying such an index; the only package with
real MariaDB coverage is `Themia.Data.Migrations`, whose `mariadb:11` advisory-lock test still runs and
whose lock semantics stay deliberately portable to it.

**How to upgrade:**

- **On MySQL 8.0.13 or newer** — nothing to do. This is a documentation correction, not a behaviour
  change.
- **On MySQL older than 8.0.13** — `Themia.Modules.Pdf` and `Themia.Challenges` will fail at migration
  time. Upgrade the server; there is no supported workaround.
- **On MariaDB** — Themia does not install. `Themia.Data.Migrations` alone still works, but any module
  whose schema uses a functional index does not. Migrate to MySQL 8.0.13+ or PostgreSQL. If MariaDB
  support matters to you, file a coord request: closing the gap means replacing every functional index
  with a persisted generated column plus an index on it, across every module that uses one, and that
  work is deferred until an adopter actually needs it.

### `Themia.Notifications`: the logger stubs no longer report success

**What changed:** `LoggerEmailSender` and `LoggerSmsSender` returned `NotificationResult.Success()`
without sending anything. They now return `NotificationResult.NoProviderConfigured(reason)` and log at
`Warning` instead of `Information`.

`NotificationResult` gained a `NotificationOutcome Outcome` property with three states — `Sent`,
`Failed`, `NotConfigured`. `Succeeded` is now computed (`Outcome == Sent`) and `NotConfigured` is a
convenience for `Outcome == NotConfigured`. Existing `if (result.Succeeded)` code keeps compiling and
keeps meaning the same thing.

**Why:** `AddThemiaNotifications()` registers those stubs with `TryAdd` so the DI graph always resolves.
That is deliberate and stays. But combined with a success result it meant a host that never configured a
real provider saw every send succeed while nothing was delivered, with no signal anywhere — and a
caller's retry or audit logic recorded deliveries that never happened. "I deliberately did not send
this" and "I sent this" must not be the same value.

**Who is affected:** any host that runs on the stub for a channel — which includes running deliberately
without SMTP, a supported and normal state. Both known consumers do this: ezy-assets falls back to the
stub when `Email:Smtp:Host` is unset, and propertiezy documents an unset `Smtp:Host` as "a normal,
supported state".

**How to upgrade.** Anywhere you treat a non-success result as a failure, separate "not configured"
from "the provider rejected it":

```csharp
// before — an unconfigured channel looked like a successful send
var result = await emailSender.SendAsync(message, ct);
if (!result.Succeeded)
{
    logger.LogError("Email delivery failed: {Error}", result.Error);
    return Problem("Could not send the email.");
}

// after — an intentionally-disabled channel is not an error
var result = await emailSender.SendAsync(message, ct);
if (result.NotConfigured)
{
    // Nothing was sent, and that was the configuration's intent.
    // Fall back to whatever you did before (log the invite link, skip the notification, ...).
    logger.LogInformation("Email delivery is not configured; skipping.");
}
else if (!result.Succeeded)
{
    logger.LogError("Email delivery failed: {Error}", result.Error);
    return Problem("Could not send the email.");
}
```

If you previously relied on `Succeeded == true` from the stub to mean "did I finish handling this",
the control-flow equivalent is `result.Succeeded || result.NotConfigured`.

> **Do not apply that substitution to anything that records or reports delivery.** Writing
> `if (result.Succeeded || result.NotConfigured) await audit.RecordDeliveredAsync(...)` produces a
> "delivered" audit row for a message that was never sent — which is the exact defect this change
> removes, restored under a new spelling. The equivalence is safe for "have I finished with this
> message", and wrong for "was this message delivered".

**If you use `Themia.Modules.Notifications` instead of calling a sender directly, you have nothing to
change — but read this.** `INotificationDispatcher.DispatchAsync` enqueues an outbox row and never hands
you a `NotificationResult`; the result is interpreted inside `NotificationOutboxDispatcher`, which is
framework code. That mapping is updated in this release: a `NotConfigured` result now dead-letters the
row on its **first** attempt rather than being treated as a retryable failure.

That matters because the naive mapping was genuinely wrong. `NotConfigured` reached
`DispatchResult.Transient`, so on a host with no configured provider every notification was retried to
the attempt cap (5 by default) and *then* dead-lettered — messages that previously completed as `Sent`
were lost, ten `Warning` lines were written per message, and with `PurgeEnabled` defaulting to `false`
the dead rows accumulated indefinitely.

Retrying cannot help here: configuration does not change between backoff attempts. Failing on the first
attempt puts the reason in `last_error` immediately, where an operator can see it. **If you run a host
deliberately without a provider and do not want dead rows, either disable the outbox for that channel or
configure a sender** — a `Dead` row is now the honest record of a message that was never sent.

**If you would rather fail fast at startup:** the framework does not currently expose a way to detect a
stub registration (`LoggerEmailSender` and `LoggerSmsSender` are `internal`). The supported approach is
to assert your own configuration — check that `Smtp:Host` (or your provider's equivalent) is set in the
environments where delivery is required, and fail startup yourself. If you want the framework to refuse
a stub outside `Development`, say so on coord #0057 and it can be added.

## 0.11.0

### `Themia.Modules.Notifications`: outbox drain plumbing moved into `Themia.Messaging`

**What changed:** two breaking changes landed together as the shared outbox/inbox core
(`Themia.Messaging`) was extracted so `Themia.Modules.Messaging` could reuse it:

1. `DrainSignal` moved from `Themia.Modules.Notifications.Outbox.DrainSignal` (non-generic) to
   `Themia.Messaging.Outbox.DrainSignal<TRow>` (generic). Both the namespace and the generic parameter
   changed, so no `TypeForwardedTo` can bridge the two.
2. Four `INotificationsSqlDialect` members — `ClaimAsync`, `CompleteAsync`, `CreateConnection`,
   `FailAsync` — moved off `INotificationsSqlDialect` itself onto the shared
   `Themia.Messaging.Outbox.IOutboxDialect<TRow>` base interface it now extends.

**Why:** `Themia.Modules.Messaging`'s outbox needed the identical claim/lease/backoff drain loop
Notifications already had; extracting it into a neutral, row-shape-generic `Themia.Messaging` core lets
both modules share one implementation instead of forking it.

**How to upgrade:**

- **No action** if you only consume Notifications through `AddThemiaNotificationsModule(...)` and its
  documented services (`IOutboxStore`, `INotificationDispatcher`, etc.) — the drain loop's behavior is
  unchanged; only its internal plumbing moved.
- **If you referenced `Themia.Modules.Notifications.Outbox.DrainSignal` directly** (e.g. to call
  `Signal()` after your own write): change the type to
  `Themia.Messaging.Outbox.DrainSignal<Themia.Modules.Notifications.Outbox.ClaimedOutboxRow>` and update
  the `using`.
- **If you implemented `INotificationsSqlDialect` directly** (a custom engine dialect): the four moved
  members now satisfy `IOutboxDialect<ClaimedOutboxRow>` instead of being declared directly on
  `INotificationsSqlDialect` — method signatures are unchanged, only where they're declared, so no
  implementation code changes.

## 0.8.x → 0.9.0

**Breaking: `IStorageProvider.GetPublicUrl(string key)`.** Only affects code that implements
`IStorageProvider` directly — the built-in Local and S3/R2 providers already do. Consumers of
`ITenantStorage` need no change.

If you implement `IStorageProvider`, add:

```csharp
public Uri GetPublicUrl(string key) =>
    throw new InvalidOperationException("This backend has no public container.");
```

**Optional:** to serve public media, configure a public container —
`PublicRootPath` + `PublicBaseUrl` (Local) or `PublicBucketName` + `PublicBaseUrl` (S3/R2) — and write with
`new StoragePutOptions(contentType, Visibility: StorageVisibility.Public)`. A relative `PublicBaseUrl` throws
at startup. **Visibility cannot be changed after a write**; delete and re-upload to move an object.

**Schema:** `storage_objects.visibility` is added by `StorageVisibilityMigration` (FluentMigrator, applied on
boot). Every existing row defaults to `Private`, which is correct: private keys are unchanged and no blob moves.

## 0.6.7

### `OidcExternalAuthProvider` and `ExternalAuthenticationFlow` are now `internal`

**What changed:** the two concrete external-auth implementations in
`Themia.Modules.Identity.ExternalAuth.AspNetCore` — `OidcExternalAuthProvider` and
`ExternalAuthenticationFlow` — changed from `public` to `internal`. They are still registered and used
exactly as before; only direct references to the concrete types break.

**Why:** both are pure DI implementations behind `IExternalAuthProvider` and `IExternalAuthenticationFlow`.
Consumers register them through `AddThemiaExternalAuth()` and resolve the interfaces, so the concrete types
were never part of the intended surface. They were briefly public in 0.6.6 (their first release); narrowing
them now — before any consumer depends on them — keeps the package surface to the interfaces.

**How to upgrade:**

- **No action** for normal use — `AddThemiaExternalAuth()`, the builder, the endpoints, and the
  `IExternalAuthProvider` / `IExternalAuthenticationFlow` abstractions are unchanged.
- **If you referenced `OidcExternalAuthProvider` or `ExternalAuthenticationFlow` directly** (only possible
  on 0.6.6), depend on the interface instead: resolve `IExternalAuthProvider` / `IExternalAuthenticationFlow`
  from DI, or register a provider via the `AddThemiaExternalAuth().AddOidc(...)/.AddProvider(...)` builder.

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
