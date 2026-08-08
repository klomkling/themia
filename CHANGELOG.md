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

_Nothing yet._

## [0.14.1] - 2026-08-08

### Added
- **`ChallengeIssueResult.RetryAfter` and `ChallengeVerifyResult.RetryAfter`** (coord #0064) — a
  `RateLimited` result now carries how long until the refused window resets. Without it an adopter could
  learn *that* it was refused and nothing about *when* it clears, and could not compute it either: the
  counter rows are Themia's and nothing on `IChallengeService` exposes them. Keeping a parallel
  "last issued at" beside ours is the bookkeeping `Themia.Challenges` exists to remove.

  ezy-assets' four one-time-code flows answer 429 + `Retry-After` with a live countdown — a contract
  **Themia built for them on coord #0001, its very first request** — so migrating those flows onto
  `Themia.Challenges` would have regressed the thing they originally asked for.

  **This is data, never policy — deliberately.** Nothing turns the value into a status code or a header
  for you, and no middleware or mapper ships with it. propertiezy's `password/forgot`,
  `email/resend-verification` and `phone/send-otp` answer *identically* whether the account exists, by
  design; "rate limited, retry in 43s" is reachable only for a key with a live window, so an automatic
  429 + `Retry-After` would rebuild the account-enumeration oracle those endpoints exist to close — on
  upgrade, with no diff on their side and no test failing. **Surface it only where the caller already
  knows the account exists** (an authenticated session, or a signing token that names the principal).
  The warning is on the property itself, not just here.

  **`null` means "not determined", not "retry now".** Do not write `?? 0` or `?? 60` — a hardcoded
  fallback is exactly the client-side guess that drifts from the server's real window, which is the
  defect this property removes. Omit the header and answer without a countdown.

  The value is the **latest** reset among the layers currently over their limit, since every configured
  layer must be under its ceiling before the next call can succeed; reporting the earliest would send a
  caller back into a refusal it could have predicted. Covered on all three refusal points (issue, verify,
  token verify). Falsified: reporting the earliest reset fails 1 test, breaking the remaining-time
  arithmetic fails 5.

## [0.14.0] - 2026-08-08

### Added
- **`Themia.PromptPay`** — PromptPay QR payload construction: EMVCo TLV assembly and CRC-16 for Credit
  Transfer (Tag 29) and Bill Payment (Tag 30) (coord #0055, from #0052 item 4). `net8.0;net10.0`, pure
  computation — no HTTP, no credentials, no clock, no I/O. QR **image** rendering is deliberately not
  included; both consumers would otherwise write EMVCo TLV and CRC-16 themselves, twice, slightly
  differently.

  **The product discriminator lives on the registration, not the call.** Two products billing under one
  Tax ID must be distinguishable, and where they are distinguished depends on what the bank issued:
  `BillerRegistration.PerProductSuffix(taxId, suffix)` when the bank gives one suffix per product, or
  `SharedSuffix(taxId, suffix, productPrefix)` when it gives one for both — in which case this package
  prepends the prefix itself, so a call site never does prefix arithmetic and never omits it.

  This replaces the guard originally promised on #0052 item 2, which took the biller id and suffix as
  separate required inputs. ezy-assets pointed out that it covers one branch of two: under a shared
  suffix both products pass the same value and the discriminator becomes a free-text prefix that nothing
  validates — the original silent collision, with a call site that now *looks* guarded. With both
  products' payments landing in one receiving account, that string is not a formatting convention with a
  safety net behind it; it is the safety net. Switching branches when the bank answers is one line at the
  composition root.

  **`MaxReferenceLength` is derived, not guessed.** An EMVCo length field is two decimal digits, so Tag
  30's whole value is capped at 99 characters, of which the AID takes 20 and a 15-digit Biller ID takes
  19 — leaving **56** for Reference 1, or 53 once a 3-character product prefix is reserved. A Reference 2
  lowers it further and `BillPayment` checks the exact total. A bank's own limit may be lower and is not
  knowable from here, so nothing invents one: `maxReferenceLength` tightens it, and may only tighten.

  Tag 29 rejects a mobile number that is not recognisably Thai rather than reinterpreting it — guessing
  there does not fail, it succeeds, at whoever holds the resulting Thai number.

  The wire format is pinned by golden vectors reproduced from an independent implementation and verified
  before any of this package existed, by recomputing every checksum with a bitwise CRC written from the
  algorithm rather than a borrowed lookup table.

  **Not built: `Themia.SlipVerification`.** Verification is gated on a provider choice that has not come
  back, and propertiezy — the consumer actually on the near path — reconciles manually through the back
  office in their first slice. Building an adapter shape for a provider nobody has picked would be guessing.
- **`IChallengeService.VerifyByTokenAsync` is implemented** (coord #0061) — it threw
  `NotSupportedException` on every call, because `IssueAsync` never populated a token hash. Everything
  else was already there and working: `ChallengeFormat.OpaqueToken`, `SecretGenerator`'s Base64Url
  generator, the `token_hash` column, `ix_challenges_token_hash`, and `SelectLiveByTokenHashSql` on all
  three dialects. Two wires were missing.

  **The surface misled in both directions.** Reflecting over the assembly showed `ChallengeFormat.OpaqueToken`
  and `VerifyByTokenAsync` side by side with nothing indicating either was inert, so a consumer trusting
  the public surface ships a guaranteed 500 on their verification endpoint — propertiezy found the throw
  by decompiling. Meanwhile #0056's "v1 ships numeric codes only" read as if opaque tokens were absent
  altogether, when issuance worked fine.

  Behaviour worth knowing before you use it:
  - **A numeric challenge can never be resolved here.** Only `OpaqueToken` rows carry a lookup hash. The
    hash has to be deterministic to be a lookup key, and a deterministic digest of a 6-digit code is a
    disclosure — so numeric keeps the salted PBKDF2 and stores nothing to look up.
  - **Wrong purpose and wrong tenant report `NotFound`,** indistinguishable from an expired link.
    Telling them apart would confirm a token exists and say where it belongs.
  - **Success discloses the key** through `ChallengeVerifyResult.Scope` — that is the point, since the
    caller has no other way to learn which principal a token-only link belongs to. Fine when the key is a
    user id its holder already owns; do not use this path when the key is itself sensitive. Every failing
    outcome reports `ChallengeScope.UnresolvedKey`.
  - **⚠ Redeem on POST, never on GET.** Email scanners and link-preview bots fetch every URL in a message
    before the recipient does, and this method consumes the challenge — so a page that redeems in its GET
    handler burns the token on the scanner's fetch and shows the real user "invalid or expired".
    propertiezy hit exactly this on their own verify page; the warning is in the XML docs because
    consumers will otherwise get it wrong.

- **`ChallengeOptions.TokenVerifyWindow`** — an optional ceiling on token lookups, bucketed by
  `(tenant_id, purpose)`. **Null by default**, and deliberately so: brute force here is bounded by 256
  bits of entropy, not by a rate limit, so this bounds store load only. It also cannot be keyed on the
  challenge's key — a token lookup does not know the key until it succeeds, which is the whole reason the
  method exists — so enabling it lets one attacker exhaust a tenant's ceiling and refuse *legitimate*
  verifications until the window rolls. That is a bad trade against a threat the entropy already handles.

- **(breaking) `IUserService.ConfirmEmailAsync`** (coord #0060) — email verification could not be completed through
  the public surface. `EmailConfirmed` was writable only as an argument to `CreateExternalUserAsync`, so
  after creation nothing could set it. Consuming an `IUserTokenService` token with
  `TokenPurpose.EmailConfirm` invalidates the token and writes nothing to the user, which means the
  framework shipped the exact token purpose the flow needs and then no way to act on a successful consume.

  propertiezy shipped a `POST /auth/email/verify` that consumed the token and answered "Email confirmed
  successfully" while `EmailConfirmed` stayed false forever. Nothing failed, nothing logged, and no test
  caught it, because the endpoint's observable behaviour was exactly what a working implementation
  produces. Their workaround reached past the service layer to write the identity-owned `User` entity from
  application code.

  The gap was ours twice over: 0.12.2 added `SetPhoneNumberAsync` **and** `ConfirmPhoneNumberAsync` for the
  phone axis while `TokenPurpose.EmailConfirm` — which predates both — kept no setter at all, and
  `ConfirmPhoneNumberAsync` itself shipped with no behaviour test. Both axes are now covered by conformance
  tests that re-read the row rather than asserting the call's return value.

- **(breaking) `IUserService.SetEmailAsync`** — not requested, and shipped anyway because `ConfirmEmailAsync` alone
  would have been the same half-a-pair mistake reported above. Since 0.12.2 a **confirmed email is a login
  identifier**, and with no service method to change an address an adopter changes it by writing the entity
  directly — which leaves `EmailConfirmed` true across the change. Editing a profile to another user's
  address would then inherit their confirmed status and log in as them.

  `SetEmailAsync` normalizes, rejects an address another user in the scope already holds
  (`SetEmailResult.Duplicate`), and **always clears `EmailConfirmed`**, including when the address is
  unchanged — the same rule and the same reasoning as `SetPhoneNumberAsync`.

## [0.13.0] - 2026-08-05

### Added
- **`Themia.Modules.Identity.Dapper` and `Themia.Modules.Identity.EFCore`** — the identity store splits into
  engine packages, the same shape `Themia.Challenges` already uses (coord #0058). Reference the core plus
  exactly one engine package.

  ezy-assets filed it after checking the package graph: adopting Identity meant taking
  `Themia.Framework.Data.EFCore` and `Microsoft.EntityFrameworkCore` into an application that has no EF
  Core anywhere, deliberately, purely to reach a store they would use through Dapper. propertiezy, who
  chose the Dapper path in #0039, had been shipping four EF packages for an engine they never call.

  The coupling turned out to be three things: one file touching EF Core, one holding the Dapper mappings,
  and one registration hook. Every service — `UserService`, `RoleService`, `ExternalLoginService`,
  `ClaimsPrincipalFactory`, the Argon2 hasher, every `Specification` — was already written against
  `Themia.Framework.Data.Abstractions`, so the store was engine-agnostic all along.

  **The core now carries no data peer, no database driver and no migration runner** — Abstractions,
  `Themia.Framework.Core`, `Themia.Framework.Data.Abstractions`, `FluentMigrator` and Argon2. That is more
  than the request asked for: EF Core leaves the graph, and so do Npgsql, MySqlConnector,
  `Microsoft.Data.SqlClient` and the three FluentMigrator runners, which now arrive only with the engine
  package that needs them.

- **`AddThemiaIdentityDapper` fails loudly when the Dapper peer has not been registered.** The core used to
  scan the service collection for an `EntityMappingRegistry` and contribute to whatever it found — so the
  Dapper path was *inferred*, and inferred wrong, silently, whenever the peer registration ran second: no
  error, no log, just identity mappings never applied until a query returned the wrong columns. That is the
  ordering hazard propertiezy cites in #0039. It is now impossible to be on the Dapper path by accident.

- **`AddThemiaIdentityEFCore` fails loudly when `ApplyThemiaIdentity` was never called.** The EF leg had
  kept the exact failure the split removed from the Dapper leg: nothing observable at registration time
  says whether the model configuration was applied, so a forgotten `OnModelCreating` line started cleanly,
  let the module migrate `identity.users` into existence, and first surfaced as a query against a table
  EF Core had never been told about. The registration now adds a startup check over the built model
  (`IdentityModelValidation`), so the mistake fails the host with the missing call named.

- **`Themia.Modules.Identity.Migrations.IdentityMigrations.Assembly`** — the core defines the `identity`
  schema but ships no runner (running migrations needs a driver per engine, and the core stays
  driver-free). Both engine modules apply it for you; an adopter who takes only the core, supplying their
  own `IRepository` implementations, now has a named handle to run instead of a `typeof(...)` on a
  migration class.

- **`DapperMappingRegistration`** in `Themia.Framework.Data.Dapper` — one mapping-contribution mechanism
  for every module. Identity, Storage, Notifications and Messaging had each hand-rolled the same
  service-collection scan and the copies had drifted into three different behaviours for one adopter
  mistake, so registering the peer after the modules produced a hard failure from one module and silently
  unmapped tables from the others in the same startup. `ContributeDapperMappings` is for modules that
  support both peers (silent only for a genuine EF adopter, loud when a Dapper peer is present without its
  registry); `RequireDapperMappings` is for Dapper-only packages, where no registry can only mean the
  wrong order.

### Changed
- **(breaking) `IdentityModule` becomes `IdentityDapperModule` / `IdentityEFCoreModule`**, in the matching
  engine package. A single module could only register the engine-agnostic core, which on Dapper meant
  mappings that were never contributed — the same silent failure, reached from the module instead of from
  the registration. One module per peer makes choosing wrong a compile error.
- **(breaking) `ModelBuilderExtensions.ApplyThemiaIdentity` moves to `Themia.Modules.Identity.EFCore`** and
  **`IdentityDapperMappings.Apply` moves to `Themia.Modules.Identity.Dapper`.** Type names and namespaces
  are unchanged, so only the package reference moves. Both consumers confirmed zero call sites, so no
  compatibility shim ships — propertiezy argued for that themselves: a forwarding type nobody uses is a
  spare compatibility surface that lets a caller keep an old assumption compiling.
- **(breaking) `AddThemiaIdentityServices` is renamed `AddThemiaIdentityCore` and the old name is a
  compile error.** Dapper adopters call `AddThemiaIdentityDapper`, EF Core adopters
  `AddThemiaIdentityEFCore`; either one calls the core for you. Call `AddThemiaIdentityCore` directly only
  when supplying your own `IRepository` implementations.

  Keeping the old name callable was the larger hazard. It used to contribute the identity mappings to a
  Dapper `EntityMappingRegistry` if it found one and no longer does, while its signature is unchanged — so
  an existing Dapper bootstrap would have recompiled with zero errors and zero warnings and then queried
  unqualified `users` instead of `identity.users`: an auth outage on first login with nothing connecting it
  to the upgrade. `[Obsolete(error: true)]` makes the call site change mechanical rather than a line in
  this file that somebody has to read.

- **(breaking) `AddThemiaDapperCore` reuses an already-registered `EntityMappingRegistry`** instead of
  registering a second one. Two registrations meant the later instance won resolution while every mapping
  the modules had contributed sat on the first, so every module-mapped table silently fell back to its
  convention name.

- **(breaking) Storage, Notifications and Messaging throw when a Dapper peer is registered without its
  mapping registry.** Previously Storage and Notifications returned quietly in that state, which is never
  a legitimate configuration — it means the module was registered before the peer, and the tables stay
  unmapped until a query fails. A genuine EF Core adopter (no registry, no `IDapperConnectionContext`) is
  unaffected.

- **(breaking) The module identifiers changed, not just the type names.** `ModuleDescriptor.Name` goes from
  `"Themia.Identity"` to `"Themia.Identity.Dapper"` / `"Themia.Identity.EFCore"`. If your host keys module
  enablement, ordering, or `ModuleDescriptor.Dependencies` off that string — a `modules` table, a
  `Modules:Themia.Identity:Enabled` config entry — update the key as well as the type, or the module reads
  as absent and `IUserService` never gets registered.

## [0.12.2] - 2026-08-05

### Added
- **`Themia.Modules.Identity` logs in by username, confirmed email, or confirmed phone** (coord #0054).
  `IAuthenticationFlow.LoginAsync`'s first parameter is now `identifier`. Resolution order is normative:
  **username, then email, then phone** — username first because it is the only identifier that has always
  been unique. Propertiezy filed this after a production incident: their login field is labelled
  "ชื่อผู้ใช้ / อีเมล" and an email returned a 401 indistinguishable from a wrong password.

  Three behaviours are load-bearing rather than incidental:
  - **All three columns are queried even after the first matches.** Stopping early would (a) miss a
    collision — per-column uniqueness cannot stop one user's username equalling another's email — and
    (b) make the number of round trips reveal which identifier space a string belongs to.
  - **An identifier matching two *different* users is refused, never resolved.** Picking either silently
    is account takeover. Hooks see `LoginFailureReason.AmbiguousIdentifier`; the caller sees the same
    uniform failure as everything else. One user matching on two columns is not a collision.
  - **Lockout and verification stay keyed on the username**, whatever the caller typed. Keying them on
    the identifier would give each of a user's three identifiers its own attempt budget.

  Email and phone match **only when confirmed** — an unconfirmed address is a claim, not proof of
  control. Every failure remains one outcome: widening the identifier space widens the enumeration
  oracle otherwise, three spaces to probe instead of one.

- **A phone number can now become a login identifier at all.** `PhoneNumber` shipped on the entity and in
  the schema but nothing ever wrote it, it had no normalized form, no uniqueness and no index — storable
  and unusable. Added: `IUserService.SetPhoneNumberAsync` / `ConfirmPhoneNumberAsync` / `FindByPhoneAsync`,
  a `normalized_phone_number` column with the same two filtered unique indexes `normalized_email` has, and
  `IPhoneNumberNormalizer` with a `FormattingOnlyPhoneNumberNormalizer` default.

  **The default deliberately does not understand phone numbers.** It strips formatting, so
  `+66 81 111 2222` and `+66811112222` are one number — but `0811112222` and `+66811112222` are **not**,
  because they are the same number only given a region the framework cannot know. Guessing it wrong in
  one direction locks a user out of their own number; in the other it merges two people's accounts.
  Supply your own normalizer over an E.164 library if you need national forms, and treat changing it as a
  data migration — the value is persisted and uniquely indexed.

  `SetPhoneNumberAsync` always clears `PhoneNumberConfirmed`, including when the number is unchanged:
  confirmation is proof of control over one number at one time, and carrying it across a write would let
  a profile edit inherit someone else's confirmed status. Themia does not verify the number — call
  `ConfirmPhoneNumberAsync` after your own proof, for which `Themia.Challenges` exists (Identity takes no
  dependency on it, so nothing here can check that you did).

### Fixed
- **`Themia.Data.Migrations` no longer replaces a migration failure with its own teardown error**
  (closes [#195](https://github.com/klomkling/themia/issues/195)). `ThemiaMigrations.Run` held the
  runner's scope and provider in `using var`. FluentMigrator's SQL Server processor disposes by calling
  `RollbackTransaction()`, which throws `InvalidOperationException("This SqlTransaction has completed")`
  whenever the transaction was already dead — and C# lets a `using` variable's dispose exception
  **replace** the one already in flight.

  So every migration that lost a deadlock or timed out on SQL Server reported the zombied-transaction
  message and **lost the `SqlException` that caused it**. An operator debugging a failed deploy saw
  "This SqlTransaction has completed" instead of a deadlock or a permission error, and the carefully
  worded wrap ("Verify the connection string and that the principal has DDL permissions") was discarded
  in precisely the case it exists for. It also silently broke every caller that retries on SQL error
  numbers, since what reached their `catch` was no longer a `SqlException`.

  Teardown now runs in a `finally` that reports a dispose failure **only when the body did not already
  fail** — a dispose failure is a consequence, never a cause. A clean run whose teardown fails still
  throws, because a processor that cannot dispose may be holding a connection or transaction open.

  This is what had been surfacing as an intermittent `Themia.Modules.Scheduling` integration failure
  under parallel container load. Two earlier attempts at it — matching the word "deadlock", then
  matching SQL error numbers, then raising the command timeout to 120s — each diagnosed the *original*
  exception correctly and each failed to close it, because that exception never arrived.

### Changed
- **(breaking) `IUserService` gained three members and `UserService`'s constructor gained a parameter**
  (`IPhoneNumberNormalizer`). Only affects code that implements `IUserService` or constructs `UserService`
  directly; DI resolves the new dependency from `AddThemiaIdentity`, which registers the default with
  `TryAdd` so an adopter's own registration wins.
- **(breaking) `LoginFailureReason` gained `AmbiguousIdentifier`**, so an exhaustive `switch` over it
  stops compiling until the case is handled.
- **`Themia.Modules.Identity` masks the identifier in failed-login logs.** The line carried a username
  verbatim; now that it may be an email address or a phone number, logging it would push PII into every
  aggregator on the highest-volume line in the flow. Hooks still receive the unmasked value — they are
  in-process adopter code and are where lockout and abuse detection belong.

## [0.12.1] - 2026-08-05

### Added
- **(breaking) `ChallengeOptions.VerifyWindow`** (default 20 per 15 minutes) — `Themia.Challenges` now rate-limits
  `VerifyAsync` per key, closing [#190](https://github.com/klomkling/themia/issues/190). `MaxAttempts`
  lives on a challenge row, so it bounds guesses against an *issued* secret and bounded nothing at all
  when none was live: wrong codes against a key whose challenge was consumed, expired or exhausted cost
  two queries each, forever, and probed the `Consumed`-vs-`NotFound` oracle for free. Counted per key
  across every purpose, charged before the lookup and never refunded, and not disableable — only
  tunable, like the issuance limiter. A refused call returns the new
  `ChallengeVerifyOutcome.RateLimited` and does **not** count against `MaxAttempts`, since nothing was
  compared. `ChallengeOptions.VerifyBucketPurpose` is reserved and rejected by `ConfigurePurpose`.

### Changed
- **(breaking) `IChallengeDialect`'s scope- and window-predicated statements are now methods taking a
  shape**, closing [#189](https://github.com/klomkling/themia/issues/189).
  `SelectLiveByScopeSql` / `SelectMostRecentByScopeSql` / `InvalidateLiveForScopeSql` take a
  `ChallengeTenancy`; `IncrementWindowSql` / `DecrementWindowSql` take a `RateWindowBucket`.

  Every one of them previously compared nullable columns with a null-safe form
  (`IS NOT DISTINCT FROM`, `<=>`, or SQL Server's OR-guard) so that one statement could cover both a
  bound and a `NULL` parameter. All three forms are **non-sargable**: no index can be seeked through
  them. Measured on PostgreSQL 16 over 200 000 rows, the `IncrementWindowSql` `UPDATE` — which
  `IssueAsync` runs two or three times per call — planned as a sequential scan removing all 200 000
  rows, 1921 buffers, **16.2 ms**; the same row through a shape-specific predicate is an index scan,
  3 buffers, **0.042 ms**. All four bucket shapes behaved identically, and the OR-guard does not
  recover it even with literal values, so this had to be a change of SQL text rather than of hint.

  The four `RateWindowBucket` members map one-to-one onto the four filtered/functional unique indexes
  the schema already created — the indexes were right; only the predicate could not reach them.

  Null-safety is unchanged: each shape states its own `IS NULL` or `= @Param` explicitly, and two
  contract tests enforce it — one asserting a `NULL` shape never compares to its parameter, one
  asserting no statement carries a null-safe form again. Both fail if a single shape regresses.

  **Custom `IChallengeDialect` implementations must be updated.** The three shipped engine packages are
  the only implementations in this repo.

## [0.12.0] - 2026-08-04

### Added
- **`Themia.Challenges`** (+ `.PostgreSql` / `.MySql` / `.SqlServer`) — the one-time challenge core:
  issues a secret bound to an opaque key, verifies it exactly once, and enforces TTL, an attempt cap,
  and three layers of rate limiting. Serves phone OTP, email OTP, magic links, email verification, and
  password reset. `IChallengeService.IssueAsync` / `VerifyAsync` / `VerifyByTokenAsync` / `RefundAsync`
  over a per-engine `IChallengeDialect` (coord #0056), plus a background `ChallengePurgeService` that
  purges expired `challenges` rows (`ChallengeOptions.ChallengeRetentionHours`, default 24) and elapsed
  `challenge_rate_windows` rows on their own — longer — elapsed-window rule, never on the same setting;
  `ChallengeOptions.PurgeEnabled` (default `true`) turns the whole thing off. Needs **no Themia data
  peer** — each engine package opens its own connection — so it is adoptable by a consumer running none
  of `Themia.Framework.Data.*`.

  Four behaviours an adopter needs to know before wiring this up:
  - **Re-issuing a challenge invalidates the outstanding one by default** (`PurposeOptions.MaxLiveChallenges
    = 1`). Without the UI saying so, this reproduces the classic support ticket: a user taps "resend", the
    *first* SMS arrives after the second was issued, and the code in that first message no longer
    verifies — it was silently superseded, not delayed.
  - **The per-key rate limit (`ChallengeOptions.PerKeyWindow`) is an account-lockout vector, not a
    security control.** Anyone who knows a phone number or email address can issue against it until the
    ceiling trips, locking out the real owner; `MaxAttempts` is what actually stops brute force. Set
    `PerKeyWindow` to bound delivery *cost*, not to "be safe", and call
    `IChallengeService.RefundAsync(result.ChallengeId!.Value)` when a send is known to have failed so
    a bounced or undeliverable message never burns the victim's quota. It lives on `ChallengeOptions`
    rather than per purpose because it is a ceiling *across* purposes: a per-purpose window would bucket
    the same key differently per purpose, handing a purpose-cycling attacker a fresh ceiling for each.
  - **`ChallengeVerifyOutcome` distinguishes `Consumed`, `Expired` and `NotFound` — do not pass that
    distinction to an anonymous caller.** It is exactly an account-enumeration oracle on a login or
    password-reset endpoint: any wrong code reveals whether the key was ever challenged, i.e. whether the
    address is registered. Branch on it internally; return one indistinguishable failure outward.
  - **`AddThemiaChallenges(...)` alone does not work.** Registering the core without also calling exactly
    one of `AddThemiaChallengesPostgres` / `AddThemiaChallengesMySql` / `AddThemiaChallengesSqlServer`
    throws, by name, the first time `IChallengeService` is resolved — deliberately, rather than falling
    back to a silent in-memory store that would pass every test and lose every challenge on restart.

  Two known gaps ship with it, both found by review and tracked rather than quietly carried:
  - **`VerifyAsync` has no rate limit of its own** ([#190](https://github.com/klomkling/themia/issues/190)).
    `MaxAttempts` lives on a challenge row, so it bounds guesses against an *issued* secret and bounds
    nothing when none is live. Put your own limit in front of an anonymous verify endpoint until this
    lands; closing it needs a counter design, not a patch.
  - **The null-safe predicates are non-sargable** ([#189](https://github.com/klomkling/themia/issues/189)),
    so the indexes are not seeked on the hot path. Correctness is unaffected; this is a query-plan issue.

- **`ChallengeOptions.PerKeyGlobalWindow`** — an optional third rate-limit layer capping issuance to one
  key **across every tenant**. `null` (off) by default. `PerKeyWindow` is bucketed by `(tenant_id, key)`
  so one tenant exhausting its ceiling cannot lock another out, and that isolation stays; but the SMS
  invoice and the victim's inbox are not partitioned by tenant, so where the tenant is attacker-influenced
  — a caller-supplied subdomain or header, and especially self-serve tenant signup — one phone number can
  be charged `PerKeyWindow`'s limit once per tenant. Turn this on for those deployments. The counter row
  uses the reserved purpose `ChallengeOptions.GlobalKeyBucketPurpose`, which `ConfigurePurpose` now
  rejects, because a platform-level challenge already occupies `(NULL, key, NULL)`.

### Changed
- **(breaking) MariaDB is no longer claimed as a supported engine.** Package descriptions, XML docs and
  migration error messages said "MySQL/MariaDB"; the MySQL leg of the shared schema uses **functional
  key parts** (`CREATE UNIQUE INDEX ... ((expr))`, MySQL 8.0.13+) to emulate the partial/filtered unique
  indexes PostgreSQL and SQL Server have natively, and MariaDB has no equivalent syntax at any version —
  so `Themia.Modules.Pdf` and `Themia.Challenges` fail at migration time on MariaDB. The claim was
  inherited across specs and never tested on any package except `Themia.Data.Migrations`, whose
  `mariadb:11` container test still runs and whose advisory-lock semantics stay deliberately portable.
  Nothing changes for MySQL adopters; the supported floor is now stated as **MySQL 8.0.13+**.

> The `Themia.Challenges` entries below record decisions made **within this release**, before the
> package ever shipped. They are not breaking changes — there is no earlier version to break — and have
> no [MIGRATION.md](MIGRATION.md) section. They are kept because each one is a design decision an
> adopter reading the API will want the reasoning for.

- **`Themia.Challenges` refund is keyed on the challenge, not the scope.**
  `RefundAsync(ChallengeScope, DateTimeOffset)` became `Task<bool> RefundAsync(Guid challengeId)`, and
  `ChallengeIssueResult` exposes `ChallengeId` rather than `IssuedAt`. The original shape was a bare decrement
  with nothing tying it to an issuance: provider delivery-status webhooks are redelivered and adopters
  retry their own failure handlers, so one failed send was refunded two or three times — and since the
  decrement floors at zero and never errors, anyone able to force deliveries to fail could replay it to
  drive the SMS cost ceiling to zero and keep issuing. The refund is now claimed with a guarded write
  against a new `refunded_at` column and returns `false` when there was nothing to refund.

- **`tests`** — one shared `Themia.TestSupport` project now owns `RecordingLogger<T>` and
  `RecordingLoggerProvider`, which had been hand-copied member-for-member into four test projects. Test
  infrastructure only; nothing shipped changes.

### Fixed
- **`Themia.Challenges` no longer burns quota for an issuance that never happened.** Both rate-limit
  counters are charged before the row is written; a failure in the invalidate or insert left them
  charged, so three transient database failures locked a real user out for the rest of the window under
  the default 3-per-15-minutes. The charges are now released on any failure, and the supersede-then-insert
  pair runs in one transaction so a failed insert can no longer kill the user's previous working code.
- **`Themia.Challenges` rate limiter fails closed when the store returns no count.** The ceiling is
  decided entirely by the value `IncrementWindowSql` returns, and `ExecuteScalarAsync<int>` mapped a
  NULL result to `0` — below every configured limit, so the issuance was admitted unconditionally and
  silently. A missing count is now an error.
- **`Themia.Challenges` pins a byte-exact collation** (`utf8mb4_bin` on MySQL,
  `Latin1_General_BIN2` on SQL Server) on every string column a dialect compares with `=`. MySQL 8 and
  SQL Server both default to case-folding collations, so a code issued for key `"A1b2"` verified against
  an account keyed `"a1b2"`, and the two shared one rate-limit bucket. Proven per engine: the new test
  fails on MySQL and SQL Server without the pin. PostgreSQL was never affected.
- **`Themia.Challenges` retention is indexed and batched.** Both purge predicates now have an index
  (`challenges.expires_at`, `challenge_rate_windows.window_start`) — each filters on a single column, so
  without one the hourly job full-scanned the tables every issue and verify contends on — and both
  statements are bounded (`LIMIT`/`TOP @Batch`) with the service looping until a batch comes back short,
  matching `Themia.Messaging`'s purge loop. An unbounded `DELETE` held locks for the whole delete.
- **`Themia.Challenges` survives a column being added to `challenges`.** Every dialect's scope and by-id
  lookups are `SELECT *`, and the Dapper column map threw on any column with no `ChallengeRow` property,
  so a later migration in this package or a DBA adding an audit column to a shared database took every
  `VerifyAsync` down. Unmatched columns are now ignored, which is what a `SELECT *` consumer should do.
- **`Themia.Challenges.MySql` pins `GuidFormat=Char36`**, matching every sibling Themia MySQL dialect.
  Both `id` columns are `CHAR(36)`; an adopter reusing a connection string carrying
  `GuidFormat=Binary16` or `OldGuids=true` wrote a mangled id that no later lookup matched.
- **(breaking) `Themia.Notifications`** — `LoggerEmailSender` and `LoggerSmsSender` reported
  `NotificationResult.Success()` having sent nothing, and `AddThemiaNotifications()` registers them via
  `TryAdd` so the DI graph always resolves. A host that never configured a real provider — or configured
  one whose settings were incomplete — therefore saw **every send succeed while no message was ever
  delivered**, with nothing in the result or the logs to distinguish that from working. The caller's own
  retry and audit logic recorded deliveries that never happened.

  Both stubs now return the new `NotificationResult.NoProviderConfigured(reason)` and log at **Warning**
  rather than Information, so the condition survives the Information-level filtering common in
  production. The email stub no longer logs the subject at all — raising the line to Warning would
  otherwise have pushed subjects, which routinely carry PII, into production log aggregators.

  `NotificationResult` now carries a three-state `NotificationOutcome Outcome` (`Sent`, `Failed`,
  `NotConfigured`) with `Succeeded` computed from it. **This is deliberately an enum rather than a second
  bool:** a bool compiles cleanly at every existing `if (result.Succeeded)` site, so nothing forces a
  consumer to revisit its mapping — which is precisely how the first attempt at this change shipped with
  `NotificationOutboxDispatcher` unrevised.

  **`Themia.Modules.Notifications` outbox mapping changed with it.** A `NotConfigured` result is now
  `DispatchResult.Permanent`, so the row dead-letters on its first attempt. Under the naive mapping it
  was `Transient`: on a host with no configured provider, every notification was retried to the attempt
  cap and then dead-lettered anyway — losing messages that previously completed as `Sent`, writing ten
  Warning lines per message, and accumulating dead rows indefinitely since `PurgeEnabled` defaults to
  `false`. Retrying cannot help, because configuration does not change between backoff attempts.

  Running without a provider stays a fully supported state. What changes is that it is no longer
  indistinguishable from delivery. See [MIGRATION.md](MIGRATION.md) — including a warning about the one
  substitution that would restore the original defect. Found while scoping what shipped here as `Themia.Challenges` (coord #0056),
  where the same path would have turned a missing SMS provider into an authentication outage with no
  signal (coord #0057).

## [0.11.0] - 2026-08-02

### Added
- **`Themia.Messaging.Hmac`** — the `themia-hmac-v1` signing scheme shared by both ends of a channel, so
  the sender and the receiver compute identical bytes: `ThemiaHmacV1` (canonicalizer, signer, timestamp
  format), per-peer key registration with rotation support (`HmacOptions.AddPeer`, multiple accepted
  inbound key ids), and `IHmacVerifier`. The canonical string is fixed and not configurable. Conformance
  is pinned by golden vectors shared across repos rather than by each side's own tests (coord #0050).
  Also carries `MessagingIdentity` — this service's one origin, read by both the outbox stamp and the
  receiving loop guard.
- **`Themia.Messaging.Http`** — the HTTP outbox dispatcher: signs and delivers a claimed row to a peer,
  and classifies the response into `Delivered` / `Transient` / `Permanent`. Redirects are **not**
  followed on peer clients — a 301/302/303 drops the signed body and a 307/308 would replay a validly
  signed payload to whatever host `Location` names (coord #0050).
- **`Themia.Messaging.AspNetCore`** — the receiving half: a minimal-API endpoint filter
  (`RequireThemiaHmac`) that verifies inbound requests, plus the loop guard that answers 200 without
  invoking the endpoint when a message has come back to its own origin. **408 (not 401) for a stale
  timestamp**, so a clock-drifted sender retries instead of dead-lettering every message — a live
  production bug on both consumers before this shipped (coord #0050, #0051).
- **`Themia.Messaging`** (+ `.PostgreSql` / `.MySql` / `.SqlServer`) — neutral transactional-outbox and
  deduplicating-inbox core shared by inter-service messaging: `IOutboxDialect<TRow>`, the generic
  `OutboxDrainer<TRow>` background service, `IInboxStore` / `IInboxAdmissionDialect`, and per-engine
  claim/complete/fail/purge/admission dialects (coord #0050).
- **`Themia.Modules.Messaging`** — tenant-aware messaging module over `Themia.Messaging`:
  `IMessageOutboxStore` / `MessageEnvelope`, the Dapper-peer deduplicating inbox
  (`AddThemiaMessagingInbox`), a FluentMigrator schema (`messaging_outbox_messages` /
  `messaging_inbox_messages`, PostgreSQL + MySQL + SQL Server), and an `AddThemiaMessagingModule` DI
  extension (coord #0050).

### Changed
- **(breaking) `Themia.Modules.Notifications`** — the outbox drainer's shared plumbing moved into the new
  `Themia.Messaging` core so both modules reuse one drain loop instead of forking it: `DrainSignal` moved
  from `Themia.Modules.Notifications.Outbox.DrainSignal` to the generic
  `Themia.Messaging.Outbox.DrainSignal<TRow>`, and four `INotificationsSqlDialect` members
  (`ClaimAsync`/`CompleteAsync`/`CreateConnection`/`FailAsync`) moved onto the shared
  `Themia.Messaging.Outbox.IOutboxDialect<TRow>` base interface it now extends. Source- and
  binary-breaking only for code that references either type directly; see [MIGRATION.md](MIGRATION.md).

  Both consumers confirmed by grep that they reference neither type, so this breaks nobody in practice.

### Fixed
- **`Themia.Messaging.Hmac` / `.AspNetCore`** — a service's identity used to be configured **twice**, in
  two packages, with nothing linking the two values: `MessagingModuleOptions.Origin` (stamped on every
  message the outbox originates) and `VerificationOptions.Origin` (what the loop guard compares the
  inbound `Origin` header against). When they drifted, a looped message matched nothing, passed the
  guard, and was reprocessed as new — no exception, no log, no failing test, and on a bi-directional
  channel an unbounded forwarding loop. Both properties are gone; one `MessagingIdentity`, registered
  once via `AddThemiaMessagingIdentity(origin)`, now feeds both halves.

  The origin is **trimmed** (HTTP strips whitespace around a header value in transit, so a padded origin
  would be stamped padded, arrive trimmed, and never match) and bounded at 100 characters to match the
  `origin` column, which nothing was checking. The loop guard logs when it drops a message —
  `Information` on a peer declared bi-directional, where stopping a loop is the design working, and
  `Warning` on any other peer, where the likeliest cause is two services sharing an origin and silently
  destroying every message between them. Turning the guard off is now an explicit
  `VerificationOptions.DisableLoopGuard` rather than a side effect of leaving a string unset.

## [0.10.2] - 2026-07-29

### Fixed
- **`Themia.SourceGenerator`** — Emit `#pragma warning disable CS8631` in generated mediator handler, registration, and dispatcher files to prevent Roslyn `CS8631` nullability constraint warnings in consumer projects (coord #0049).

## [0.10.1] - 2026-07-27

### Added
- **`Themia.Pdf`** — `ThemiaPdfOptions.MaxConcurrency` bounds how many renders run at once (coord #0046).

### Fixed
- **(breaking-ish) `Themia.Pdf`** — concurrent renders were **completely ungated**. The `SemaphoreSlim(1,1)` in
  `PuppeteerPdfRenderer` guards *browser launch* only (so concurrent first-callers don't start two Chromiums);
  `RenderHtmlAsync` then went straight to `browser.NewPageAsync()` with no limit, so every caller opened its own
  Chromium page and worst-case memory was a function of inbound traffic. On a small or shared host that
  surfaces as Chromium OOM-killing a *neighbouring* process, not as a failed render. Unchanged since 0.6.0, so
  every version up to 0.10.0 is affected.

  Renders are now bounded by `MaxConcurrency`, **default 2** — deliberately a small bound rather than
  "preserve current behaviour", since the previous behaviour is the defect. Callers beyond the limit queue and
  honour their `CancellationToken`. Raise it once you have measured a page's real cost on your host; PDF
  rendering is normally low-rate and bursty, so a low ceiling costs little latency for a predictable memory
  envelope. If you already cap concurrency yourself, set it to match rather than stacking two gates.

  **The bound is per process.** It is a `SemaphoreSlim`, so behind a load balancer — or with several
  applications sharing a host — the real ceiling is `instances × MaxConcurrency`, and since the renderer is a
  singleton per process each instance runs its *own* Chromium too. A host's worst case is
  `instances × (browser baseline + MaxConcurrency × page cost)`. For a hard guarantee that rendering cannot
  starve a *neighbouring* process, set a container memory limit as well; no in-process bound can protect a
  process it does not live in.

  The class doc also claimed the browser was "guarded by a semaphore", which reads as though renders were
  guarded — that wording is what led an adopter to assume a bound existed. Both gates are now named and
  distinguished explicitly.

## [0.10.0] - 2026-07-26

### Added
- **`Themia.AspNetCore.DataProtection`** (+ `.PostgreSql` / `.MySql` / `.SqlServer`) — a shared Data Protection
  key store for multi-instance applications (coord #0042). ASP.NET Core ships EF Core and Redis key
  repositories but no Dapper one, so on the Themia Dapper stack the default is per-container filesystem keys:
  the moment a second instance starts the key rings diverge, and auth cookies, antiforgery tokens, and anything
  else wrapped by a `DataProtector` stop round-tripping across instances. This is an ASP.NET *provider* gap, not
  a new persistence layer — distinct from the rejected coord #0039.

  Registered as an `IDataProtectionBuilder` extension, mirroring the built-in `PersistKeysToDbContext`:
  `services.AddDataProtection().SetApplicationName("app").PersistKeysToThemiaPostgres(cs)`. Calling it twice is
  last-wins, as with the built-in `PersistKeysTo*`. Follows the `Themia.Exceptional` shape — a neutral core
  with an `IDataProtectionKeyDialect` seam plus one package per engine — over **one** `data_protection_keys`
  schema owned by FluentMigrator. Keys are per *application*, not per tenant, so the package takes no
  multi-tenancy dependency. `created_at` comes from the server clock, never the application's, since a fleet's
  clocks disagree. On .NET 10 the repository also implements `IDeletableXmlRepository`, so
  `IKeyManager.DeleteKeys` works and revoked key material can be removed; .NET 8 has no such framework API, so
  that leg's public surface differs by design.

  **Two applications must not share one table.** Everything using this repository shares the whole key ring —
  each holds the raw key material able to decrypt the other's payloads, and a revocation or expiry in one
  applies to the other. `SetApplicationName` only sets the discriminator folded into the purpose chain; it is
  **not** an isolation boundary. Give separate applications separate tables or databases.

  A row that will not parse **fails the read** rather than being skipped. Skipping is unsafe in two
  directions: the row may be a `<revocation>` rather than a `<key>`, so dropping it silently reinstates key
  material an operator revoked after a compromise; and if the whole table is unreadable (a charset change, a
  mangled restore) the survivors are an *empty* ring, which Data Protection cannot distinguish from a fresh
  deployment — it mints a new key and signs out every user while the application reports healthy. The built-in
  filesystem and registry repositories fail closed for the same reason.

  **The stored key material is not encrypted at rest.** Anything that can read the table can decrypt that
  application's cookies. ASP.NET Core's own EF Core and Redis providers behave the same way, but a database
  spreads the material further than a per-instance filesystem does — into backups, replicas, and any DBA's
  reach. Treat the table as a secret, and add `ProtectKeysWith*` where the deployment requires encryption at rest.

## [0.9.1] - 2026-07-26

### Fixed
- **`Themia.Data.Migrations`** — `ThemiaMigrations.Run` now serializes migrate-on-boot across
  simultaneously-starting instances (coord #0041). FluentMigrator skips migrations already recorded in
  `VersionInfo`, so re-running is a no-op *once applied*; the unsafe window is N instances booting at once,
  all reading `VersionInfo`, all seeing the same migration pending, and all applying it concurrently —
  check-then-apply is not atomic across connections, so they collide on DDL and insert duplicate version
  rows. `Run` now holds the engine's session-level advisory lock over `MigrateUp` on a dedicated connection
  (`pg_advisory_lock` / `GET_LOCK` / `sp_getapplock`), so the first instance applies and the rest wait, then
  find the work done. No separate migration job is needed and no consumer code changes.

  The lock key is derived from the **database name**, not a fixed constant: PostgreSQL advisory locks and
  MySQL's `GET_LOCK` are keyed *server-wide* rather than per database, so a fixed key would make two
  unrelated Themia applications sharing one server queue behind each other's migrations. The key is a
  SHA-256 digest of the scope rather than `string.GetHashCode()`, which is randomized per process on .NET
  and would have every instance contend on a *different* key — a lock that silently protects nothing.

  Waits are **bounded** (default 15 min, see `ThemiaMigrationOptions.LockTimeout`) rather than infinite. An
  infinite wait sounds right — a booting instance genuinely cannot continue until the one ahead finishes — but
  it blocks a thread that cannot observe a `CancellationToken` and emits nothing, so a replica waiting on a
  wedged holder is killed by its orchestrator's startup probe and crash-loops with no diagnostic. Since a probe
  budget is typically far shorter than any sane timeout, the timeout alone is not enough: pass
  `ThemiaMigrationOptions.Logger` to get one message naming the lock before the wait begins. New overload
  `Run(engine, connectionString, options, assemblies)` carries both; the existing three-argument overload is
  unchanged and keeps the defaults.

  MySQL sends a **positive** `GET_LOCK` timeout: a negative timeout means "wait forever" on MySQL 8 but is not
  portable to MariaDB, which this engine also covers — there is now a MariaDB container leg proving it.
  SQL Server's `sp_getapplock` guard **fails closed** (anything that is not an explicit non-negative status is
  treated as *not* granted, so `MigrateUp` can never run believing it holds a lock it does not). The release
  result is now read rather than discarded: "you did not hold this lock" means the lock session was reaped
  mid-migration and mutual exclusion was not actually in force, which is logged as a warning naming the scope.
  The lock connection is **unpooled**, so it no longer occupies a pool slot for the whole migration — which
  would otherwise deadlock a deployment configured with a maximum pool size of one. A failing release can no
  longer mask the migration exception, and lock failures are reported separately from DDL failures instead of
  telling operators to audit DDL grants for a migration that never started.

  Applies to all three engines Themia ships a processor for.

## [0.9.0] - 2026-07-16

### Added
- **`Themia.Storage` / `Themia.Storage.S3` / `Themia.Modules.Storage`** — permanent, unsigned, **absolute**
  public URLs for world-readable media (coord #0022). `StoragePutOptions.Visibility` selects a container at
  write time; `ITenantStorage.GetPublicUrlAsync(key)` returns the URL, resolved at *read* time from a
  configured absolute `PublicBaseUrl`. A presigned URL is a *time-boxed capability* and is not a substitute:
  an expiring URL breaks OG/Twitter previews on a shared listing (the share is permanent, the URL is not),
  403s a crawler that re-fetches later, and defeats CDN caching because every render mints a fresh cache key.
  Visibility is a property of the **container**, not the object, because R2 has no per-object ACL and S3
  Object Ownership defaults to *bucket owner enforced*, which disables object ACLs entirely — so a per-object
  flag would silently no-op on both real backends. Configure `PublicRootPath` + `PublicBaseUrl` (Local) or
  `PublicBucketName` + `PublicBaseUrl` (S3/R2); a relative `PublicBaseUrl` now throws at **startup**.
  Public objects on Local are served from a new **ungated** `GET {mount}/public/{**key}` route with
  `Cache-Control: public` — deliberately the opposite of the dashboards' `no-store`, because these bytes are
  not sensitive. On S3/R2 they are served straight from the public bucket and never reach the app.

### Changed
- **(breaking)** **`IStorageProvider` gains `Uri GetPublicUrl(string key)`.** Affects only code that
  *implements* the interface directly; every consumer of `ITenantStorage` is unaffected. See `MIGRATION.md`.
- **`ITenantStorage.GetUploadUrlAsync`** takes a `StorageVisibility` (defaulting to `Private`), so a presigned
  upload lands directly in the right container.
- **Visibility is immutable once written.** There is deliberately no move/flip operation: private→public is
  unnecessary (keys are unguessable GUIDs), and public→private is an illusion (a CDN and Google's cache keep
  serving the copy they already have). Because no operation spans two containers, the design has **no
  partial-failure state** — no half-moved object, no orphaned blob, no reconcile sweep. Re-putting an existing
  key with a different `Visibility` throws rather than silently orphaning the old blob or ignoring the caller.
- Private physical keys are **unchanged**, so **no existing blob moves** and no data migration is required.

### Security
- **`Themia.Modules.Storage`** — the public serving route sets `X-Content-Type-Options: nosniff` and
  `Content-Security-Policy: sandbox; default-src 'none'` so a public object stored with an executable content
  type (`text/html`, `image/svg+xml`) cannot execute script in the app's origin when served same-origin (the
  Local backend). Adopters serving user-uploaded media publicly should also restrict `AllowedContentTypes` to
  a media allowlist.

## [0.8.8] - 2026-07-14

### Fixed
- **`Themia.Modules.Storage`** — the Local presigned-transfer routes (`_local/get`, `_local/put`) no longer
  sit in the route group returned by `MapThemiaStorageEndpoints`, so an adopter calling the documented
  `.RequireAuthorization()` on that group no longer gates them. A presigned URL is **self-authorizing** —
  the HMAC token *is* the credential, exactly as an S3/R2 presigned URL is — and it is handed to a browser
  (an `<img>` src, a direct upload) that carries no app session. Gating it behind app auth returned 401 for
  a *valid* signed URL, and made Local silently behave differently from S3/R2, whose presigned URLs never
  reach the app at all. The broker endpoints (mint URL, complete, download-url, delete) stay in the returned
  group and are still gated by `.RequireAuthorization()` — a test now pins both halves.

## [0.8.7] - 2026-07-14

### Fixed
- **`Themia.Exceptional.AspNetCore`** — the exceptions dashboard now derives the `CustomFavicon` link's
  `type` from the URL extension (`.svg` → `image/svg+xml`, `.png` → `image/png`, `.ico` → `image/x-icon`, …),
  omitting it when unrecognized. 0.8.5 added this to `Themia.Quartz` only, so the two dashboards disagreed:
  the exceptions one still emitted a bare `<link rel="icon" href="…">`. Not cosmetic for an SVG icon — the
  `type` hint is how a browser decides it can use an SVG at all, so an adopter pointing `CustomFavicon` at
  `/favicon.svg` got their icon on the jobs dashboard and none on the exceptions dashboard.

## [0.8.6] - 2026-07-13

### Security
- **`Themia.Quartz`** and **`Themia.Exceptional.AspNetCore`** — dashboard HTML is now served with
  `Cache-Control: no-store, no-cache, must-revalidate` (+ `Pragma: no-cache`). Neither dashboard set any
  cache header, so a page behind the `Authorize` gate stayed in the browser's cache: after an admin session
  timed out, the rendered dashboard could still be re-displayed from the **back/forward cache** — served
  from memory **without contacting the server at all**, so `Authorize` never ran and the stale page looked
  live. `no-store` also disables bfcache in Chrome/Firefox, which is the mechanism in play. The gate itself
  was never bypassed: a request that reaches the server has always been denied correctly. Static dashboard
  assets (CSS/JS/icons) stay cacheable — they are not sensitive.

### Added
- **`ThemiaQuartzOptions.OnDenied`** and **`ExceptionalDashboardOptions.OnDenied`** — optional hook run when
  `Authorize` denies a request, instead of returning the bare deny status. Lets the host redirect an expired
  session to its login page; previously a timed-out admin landed on a blank 404 with no explanation. Opt-in:
  `null` (the default) keeps the existing route-hiding 404 / `DeniedStatusCode`. A hook that throws still
  fails closed with that status — it can never serve the dashboard — and a genuine not-found (unknown
  exception id) does not invoke it.

## [0.8.5] - 2026-07-13

### Fixed
- **`Themia.Quartz`** — a configured `CustomFavicon` now actually wins the browser tab. It was emitted
  *alongside* the seven bundled PNG favicons, which declare explicit `sizes` while the adopter's link
  declared none — so the browser's size-preference algorithm picked a bundled icon and setting the option
  looked like a no-op. A set `CustomFavicon` now **replaces** the bundled set instead of competing with it
  (an adopter who supplies an icon has opted out of ours); when unset, the bundled favicons are emitted as
  before. The link's `type` is also derived from the URL extension (`.svg` → `image/svg+xml`, `.png` →
  `image/png`, `.ico` → `image/x-icon`, …) and omitted when unrecognized, rather than being absent (0.8.4)
  or hardcoded to `image/x-icon` (≤ 0.8.3, which mis-declared a PNG or SVG). Completes the fix started in
  0.8.4: guarding the empty `href` stopped a *broken* link from displacing the bundled icons, but left a
  *working* one unable to displace them.

## [0.8.4] - 2026-07-12

### Fixed
- **`Themia.Quartz`** — the jobs dashboard no longer emits an empty `CustomFavicon`/`CustomStyleSheet`
  `<link>`. Both were emitted unconditionally, so an adopter who left the option at its default (`""`)
  shipped `href=""` — which the browser resolves to the page URL itself and fetches the dashboard HTML as
  an icon/stylesheet. The favicon link is emitted **last**, so it won and displaced the seven bundled PNG
  favicons for every adopter who never touched the option. Both links are now omitted when unset (parity
  with `Themia.Exceptional.AspNetCore`, which already guarded this). The icon link also no longer hardcodes
  `type="image/x-icon"`, which mis-declared the MIME type of an adopter's PNG.

### Changed
- **`Themia.Quartz`** — the dashboard `<footer>` and its link carry `dashboard-footer` classes with their
  colours in `Content/Site.css`, instead of inline `style=` attributes. Completes the 0.8.1 reclassing: the
  footer was the last place an adopter stylesheet still needed `!important`, and without it a dark theme
  ended the page in a light-grey strip.
- **`Themia.Exceptional.AspNetCore`** — new `ExceptionalDashboardOptions.Heading` drives the list page's
  `<h1>`, falling back to `Title` when unset (so existing behaviour is unchanged). `Title` now drives only
  the document title. Lets an adopter whose injected `BodyStartHtml` header bar already carries the branding
  keep an unambiguous browser-tab title (`Title = "Contoso Exceptions"`) without the page restating it
  (`Heading = "Exceptions"`).

## [0.8.3] - 2026-07-12

### Fixed
- **`Themia.Quartz`** — `AddThemiaQuartz` is now **additive**: every `configure` delegate is applied to the
  same `ThemiaQuartzOptions` instance, in call order (last writer of a given property wins). It previously
  built a fresh options object per call and registered it with `TryAddSingleton`, so the **first** call won
  outright and every later call's settings were discarded silently — no exception, no warning. This made the
  dashboard unconfigurable for anyone consuming it through `Themia.Modules.Scheduling`, which itself calls
  `AddThemiaQuartz` to wire `VirtualPathRoot`/`Authorize`: an app that then called `AddThemiaQuartz` to set
  `HeadHtml`/`CustomStyleSheet`/`ProductName` had those dropped, and an app that called it *first* instead
  lost the module's routing and authorization wiring (dashboard mounted at the wrong path, left deny-all).
  Apps calling `AddThemiaQuartz` exactly once are unaffected.

## [0.8.2] - 2026-07-12

### Fixed
- **`Themia.Exceptional.AspNetCore`** — the dashboard now emits
  `<meta name="viewport" content="width=device-width, initial-scale=1">`. Without it a mobile browser
  laid the page out at ~980px and zoomed out to fit, leaving the dashboard unreadable on a phone (the
  jobs dashboard has always emitted one). It is the same tag for every adopter, so it belongs in the
  default chrome rather than in each adopter's `HeadHtml`; it is emitted *before* `HeadHtml`, so an
  adopter wanting a different viewport policy can still override it.

## [0.8.1] - 2026-07-12

### Added
- **`Themia.Quartz`** and **`Themia.Exceptional.AspNetCore`** — `HeadHtml` and `BodyStartHtml` on
  `ThemiaQuartzOptions` / `ExceptionalDashboardOptions`: raw-HTML slots emitted verbatim at the end of
  the dashboard `<head>` (after the built-in CSS and `CustomStyleSheet`, so they override both) and
  immediately after `<body>` opens. They close the gaps CSS alone cannot — a back-link to the host app,
  a theme toggle, a header bar on the exceptions page, a `<meta name="viewport">`. Both default to empty
  and are **not encoded**: trusted, adopter-authored markup only, never built from user input.

### Changed
- **`Themia.Exceptional.AspNetCore`** — the list page now emits `<table class="errors">` with
  `<thead>`/`<tbody>` and wraps pagination in `<nav class="pager">` (was a bare unclassed `<table>` and
  `<p>`), so adopter stylesheets get stable hooks instead of positional selectors like
  `body > p:last-of-type`.
- **`Themia.Quartz`** — the scheduler dashboard's status tiles carry `stat-executed` / `stat-failed` /
  `stat-executing` / `stat-activity` / `stat-counts` classes and their colours moved from inline
  `style=` attributes into `Content/Site.css`, so an adopter stylesheet can restyle them without
  `!important`.

## [0.8.0] - 2026-07-11

### Added

- `Themia.Framework` metapackage — assembly-less bundle of the framework core set (Core, Logging,
  Caching, Services, MultiTenancy, Mediator, MultiTenancy.Mediator, Data.Abstractions,
  Framework.AspNetCore). Deliberately excludes the data peer: adopters add exactly one
  `Themia.Framework.Data.{EFCore|Dapper}.{provider}` package. Quickstart = two references.
- README "Which packages do I reference?" section — quickstart + scenario matrix + peer-coupling
  caveat.

## [0.7.2] - 2026-07-09

### Fixed
- **`Themia.Modules.Export`** (#147) — the EF `ExportDbContext` was registered through a **singleton**
  `IDbContextFactory` + a scoped bridge, so its scoped `ITenantContext` ctor dependency resolved from the
  **root** provider (a `ValidateScopes` crash in Development; wrong/absent tenant in production). Now
  registered scoped via `AddDbContext`, so tenancy resolves from the request/job scope. Same fix already
  shipped for `Themia.Modules.Pdf` in 0.7.0.

## [0.7.1] - 2026-07-08

### Added
- **`Themia.Exceptional.AspNetCore`** — `ExceptionalDashboardOptions.CustomStyleSheet` and
  `CustomFavicon`: inject an app stylesheet/favicon into the exceptions dashboard `<head>` (the custom
  stylesheet is emitted after the built-in CSS so its rules win), letting the dashboard match the host
  app — parity with the jobs dashboard's `ThemiaQuartzOptions.CustomStyleSheet`/`CustomFavicon`.

## [0.7.0] - 2026-07-07

### Added
- **`Themia.Framework.Data.Dapper`** — `ITenantQueryFactory.For<T>(bool includeGlobalRecords)`
  per-query global-inclusion override.
- **`Themia.Modules.Pdf`** — tenant-aware HTML/PDF template store (`net10.0`) with global-default
  fallback and a render-by-key service over the neutral `Themia.Pdf`. EF Core peer (SQL Server,
  PostgreSQL) + Dapper peer (SQL Server, PostgreSQL, MySQL); one FluentMigrator schema owns
  `pdf_templates` for both peers.

## [0.6.9] - 2026-06-29

### Added
- **`Themia.Modules.Export`** — tenant-aware async export module (`net10.0`). Exports are defined
  via `IExportDefinition<TParams>` (keyed, registered by DI), triggered on-demand or on a cron
  schedule via Quartz, and delivered as a signed Storage link. Completion and failure events
  dispatch Notifications. A background cleanup job purges runs older than 7 days, and module startup
  reconciles runs left in `Running` by a host restart (older than `StaleRunGracePeriod`) to `Failed`.
  FluentMigrator schema covers PostgreSQL and SQL Server (the engines with an EF Core provider today).
- **`IDataFilterScope.BypassSoftDeleteFilter()`** (`Themia.Framework.Data.EFCore` and
  `Themia.Framework.Data.Dapper.*`) — scoped opt-in that suppresses the `IsDeleted = false`
  global query filter for a single export run when `IExportDefinition.AllowsIncludeSoftDeleted`
  is `true`. Not intended for general use outside the export pipeline.

## [0.6.8] - 2026-06-24

### Added
- **`Themia.Export`** — neutral tabular-data export contract + CSV writer: typed `ExportColumn<T>`
  selectors, report headers, and computed summary rows (`AggregateKind`). `net8.0;net10.0`, no
  framework dependency, Serenity-free. `AddThemiaExport()` registers `ICsvExporter`.
- **`Themia.Export.Excel`** — ClosedXML `.xlsx` backend over the same contract: themed tables,
  per-column number format/alignment, computed summary rows, and font-free column sizing (no
  full-sheet auto-fit). `AddThemiaExcelExport()` registers `IExcelExporter`.

## [0.6.7] - 2026-06-23

### Added
- `AuthResponse.FromTokens(AuthTokens)` (in `Themia.Modules.Identity.Abstractions`) — a single mapper
  for the `AuthTokens.ExpiresInSeconds` → `AuthResponse.ExpiresIn` wire bridge, replacing the positional
  construction repeated at the login, refresh, and external-login endpoints.

### Changed
- **(breaking)** `OidcExternalAuthProvider` and `ExternalAuthenticationFlow` (in
  `Themia.Modules.Identity.ExternalAuth.AspNetCore`) are now `internal`. They are pure DI implementations
  resolved through `IExternalAuthProvider` / `IExternalAuthenticationFlow`; consumers never need the
  concrete types. See [MIGRATION.md](MIGRATION.md#067).

### Fixed
- Documentation: the 0.6.6 MIGRATION note now warns bundled consumers that external login is no longer
  auto-wired by `AddThemiaIdentityAspNetCore` — they must call `AddThemiaExternalAuth()` (and
  `ValidateThemiaExternalAuth()` to fail-fast) or the endpoint 500s on first request.

## [0.6.6] - 2026-06-23

### Added
- **`Themia.Modules.Identity.Tokens.AspNetCore`** — persistence-free JWT access-token issuance, extracted
  from `Themia.Modules.Identity.AspNetCore`: `AccessTokenService` (the default `IAccessTokenService`),
  symmetric signing, `JwtOptions`, the shared `AuthTokenIssuer`, and the `AddThemiaIdentityTokens`
  registration. Depends only on `Themia.Modules.Identity.Abstractions` — no user store required.
- **`Themia.Modules.Identity.ExternalAuth.AspNetCore`** — external OAuth/OIDC login, extracted from
  `Themia.Modules.Identity.AspNetCore`: `OidcExternalAuthProvider`, the provider registry, the
  `AddThemiaExternalAuth` builder, the external-login flow/hooks, `MapIdentityExternalAuthEndpoints`, and
  the `ValidateThemiaExternalAuth` external-only DI guard (requires `IExternalLoginService` + the token
  seams but **not** `IUserService`). Depends only on `Themia.Modules.Identity.Abstractions`, enabling
  bring-your-own-user-store external login.

### Changed
- **(breaking)** The external-auth and JWT-issuance types — plus the `AuthResponse` response record —
  moved out of `Themia.Modules.Identity.AspNetCore` into the two new packages (namespace changes);
  `AuthResponse` now lives in `Themia.Modules.Identity.Abstractions.Authentication`. Bundled consumers
  update `using` directives only — `Themia.Modules.Identity.AspNetCore` re-references both new packages, so
  the types stay available. See [MIGRATION.md](MIGRATION.md#066).

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

