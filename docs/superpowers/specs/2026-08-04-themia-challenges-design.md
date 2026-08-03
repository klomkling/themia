# Themia.Challenges — one-time secrets, one core

**Date:** 2026-08-04
**Status:** approved (rev 2 — rev 1 reworked after scrutiny; see "What rev 1 got wrong")
**Tracks:** coord #0056 (filed as `Themia.Otp`), and item (2) of coord #0054
**Renames:** the package accepted on #0056 as `Themia.Otp` ships as **`Themia.Challenges`**.

## Problem

Propertiezy needs passwordless phone login and has a stopgap: `IOtpService` over a `Dictionary`, with
no rate limiting, no attempt cap, and no persistence. They described it themselves as deliberately naive
and would rather delete it than harden it.

Everything that makes a one-time secret *safe* is generic — rate limiting, attempt caps, TTL, single-use
invalidation, constant-time comparison, a store that survives a restart. None of it varies by SMS
provider, and none of it is specific to phones. Left to each app, each app writes its own; one of them
already did, badly.

## Decision

One neutral core that issues a secret bound to an opaque key, verifies it exactly once, and enforces the
policy around it. It knows nothing about SMS, email, or users.

```
Themia.Challenges              the core: issue / verify / consume, rate limit, attempt cap, TTL, hashing
Themia.Challenges.Dapper       persistent store over the Dapper peer
Themia.Challenges.EFCore       persistent store over the EF peer
```

### Why not `Themia.Otp`

The generic operation is *"issue a secret bound to a key, verify it once, within a TTL, under a rate
limit."* Same mechanism for **phone OTP**, **email OTP**, **magic links**, **email verification**,
**password reset**, **2FA enrolment**. `Otp` would make four of those six read as a misuse of an
SMS-shaped package. Renaming is free now — no code exists.

The key is an **opaque string**. The core never parses it, never validates its shape, never decides what
it means. Email OTP is therefore the same core with a different key and a different sender — the adopter
passes the secret to `IEmailSender` instead of `ISmsSender` (both already ship in `Themia.Notifications`).

## What rev 1 got wrong

Recorded because both errors were the kind that get re-introduced by someone reading only the final text.

1. **The API could not express a magic link**, while the spec claimed the core supported them. `Verify`
   took the key as its first argument, but a user clicking a link presents only a token — the system has
   no key. The two ways out were to put the key in the URL (leaking an email or phone into browser
   history and referrers, the very threat the spec listed) or to ask the user to retype their email
   (at which point it is not a magic link). Fixed by `VerifyByTokenAsync`, below.
2. **Multi-tenancy was cut as "speculative" on a false premise** — that keys are globally unique in
   practice. `Themia.Modules.Identity.Abstractions/Entities/User.cs:10` declares
   `User : SoftDeletableEntity<Guid>, ITenantEntity` with a nullable `TenantId`, and its own doc says
   "Tenant-scoped when `ITenantEntity.TenantId` is set". Two tenants holding the same phone number is
   the design, not an edge case. Without tenant in the challenge identity, tenant B could verify a code
   issued to tenant A — cross-tenant account takeover — and either tenant could rate-limit the other out
   of logging in. Fixed by making tenant part of `ChallengeScope`.

## Public API

```csharp
namespace Themia.Challenges;

/// Identity of a challenge. Tenant is part of it: two tenants may hold the same phone number.
public sealed record ChallengeScope(string Key, string Purpose, string? TenantId = null);

public interface IChallengeService
{
    // Rate-limited (see below). The returned secret is the only time the plaintext exists.
    Task<ChallengeIssueResult> IssueAsync(ChallengeScope scope, CancellationToken ct = default);

    // For user-typed secrets (numeric codes): the caller knows the key, so the row is found by scope
    // and the code is then compared in constant time.
    Task<ChallengeVerifyResult> VerifyAsync(ChallengeScope scope, string code, CancellationToken ct = default);

    // For opaque tokens (magic links): the caller has ONLY the token. Safe to look up by the token's
    // hash precisely because the token carries 256 bits — a numeric code must never use this path.
    Task<ChallengeVerifyResult> VerifyByTokenAsync(
        string token, string purpose, string? tenantId = null, CancellationToken ct = default);
}

public enum ChallengeIssueOutcome { Issued, RateLimited }
public enum ChallengeVerifyOutcome { Verified, Incorrect, Expired, Consumed, AttemptsExhausted, NotFound }
```

Both results are **enums with computed booleans**, never bare bools — a caller must not collapse
`RateLimited` or `AttemptsExhausted` into "not verified" and treat them the same. (`Themia` already
models outcomes this way: `DispatchOutcome`, `LoginOutcome`, `NotificationOutcome`.)

`ChallengeVerifyResult` carries the verified `ChallengeScope` on success, so a `VerifyByTokenAsync`
caller learns which key the token belonged to — that is how a magic-link endpoint knows who just logged
in without the key ever travelling in the URL.

### Purpose is the configuration unit

```csharp
services.AddThemiaChallenges(o =>
{
    o.ConfigurePurpose("login", p =>
    {
        p.Format = ChallengeFormat.Numeric(6);
        p.Ttl = TimeSpan.FromMinutes(5);
        p.MaxAttempts = 5;
        p.PerScopeWindow = (Limit: 3, Window: TimeSpan.FromMinutes(15));
    });
});
```

`purpose` scopes a challenge so a code issued for `"login"` cannot be replayed against `"change-phone"`,
**and** it is where TTL, attempt cap, format and limits are set. This is what makes "email OTP costs
nothing" true at the policy level and not just the mechanism level: email is slower to arrive than SMS
(queueing, greylisting) and costs almost nothing to send, so it wants a longer TTL and a looser limit
than an SMS purpose. Same core, different purpose config.

## Two challenge shapes

| | Numeric code (OTP) | Opaque token (magic link) |
|---|---|---|
| User types it | yes — must be short | no — travels in a URL |
| Entropy | ~20 bits (6 digits) | 256 bits |
| Brute-forceable | **yes** — attempt cap is load-bearing | no |
| Forwardable | hard (short TTL, needs the code) | **trivially — forwarding the email grants login** |
| Verify path | `VerifyAsync(scope, code)` | `VerifyByTokenAsync(token, ...)` |

**v1 ships the numeric format and `VerifyAsync` only.** `ChallengeFormat.OpaqueToken` and
`VerifyByTokenAsync` are specified and present in the API from the start — they cost almost nothing at
design time and are expensive to retrofit — but the token generator is not implemented until an adopter
asks. Nobody has.

### The prefetch trap, recorded now rather than discovered later

The most common way magic links fail in production: **email scanners follow the link before the user
does.** Outlook Safe Links, corporate antivirus and Slack unfurling all issue a `GET` on every URL in a
message. With a naive single-use `GET`, a scanner consumes the token and the real user sees "link
expired" every time — a support problem that is very hard to trace to its cause.

**The rule, when magic links ship:** a `GET` on the link must be idempotent and must NOT consume the
challenge — it renders a confirmation page. The `POST` behind the user's click consumes it. Scanners do
not POST. The core cannot enforce this (it never sees the HTTP verb), but this is where someone will
look for it.

## Security requirements — non-negotiable

1. **Store a hash, never the secret.** `IssueAsync` returns the plaintext once; the row keeps a salted
   hash.
   **What this does and does not buy, stated precisely** so nobody later relaxes the TTL on the strength
   of it: hashing prevents casual disclosure — a support engineer reading the table, a code appearing in
   a query log, a screenshot of a DB browser. It does **not** protect a numeric code against an attacker
   who has the backup: 10⁶ candidates fall to a GPU instantly regardless of salt. What makes a leaked
   row worthless is the **short TTL and single-use** — the hash is defence in depth, not the defence.
2. **Rate limit in two layers, both required.**
   - *Per scope* (`tenant + key + purpose`) — the UX limit: "you asked for a login code three times in
     fifteen minutes".
   - *Per key across all purposes* (`tenant + key`) — the **cost ceiling**. Without this layer an
     attacker cycles `login` → `reset` → `verify` → `enroll` and multiplies their SMS volume by the
     number of purposes the system defines, never touching a per-purpose limit. The per-purpose layer
     protects the user's experience; only this layer protects the invoice.
   - Per IP is deliberately NOT the mechanism: the attacker rotates IPs, and the victim is the account
     owner's phone number and your SMS bill.
3. **Attempt cap per challenge.** A 6-digit code with unlimited guesses is not a second factor.
   Exceeding the cap burns the challenge.
4. **Single use, atomically.** Verification and consumption are one database operation, never
   read-then-write. Two concurrent verifications must not both succeed.
5. **Invalidate on re-issue.** A new secret for the same scope invalidates the outstanding one.
6. **Constant-time comparison** (`CryptographicOperations.FixedTimeEquals`) on the numeric path.
7. **Short TTL**, default 5 minutes for numeric.
8. **The rate limiter and attempt cap cannot be disabled.** Values are tunable; the mechanism is not
   removable. An off switch is how it ships disabled by accident.

### Why not `System.Threading.RateLimiting`

.NET 8+ ships it and it is not used here: it is **in-process and in-memory**. Behind a load balancer,
each instance would hold its own counter, so the real ceiling becomes `instances × limit` and an attacker
simply spreads requests across instances. The limit that protects an SMS bill has to be as durable and as
shared as the challenge store itself, so it lives in the same table.

## Store: no silent in-memory default

`AddThemiaChallenges()` registers the policy engine. It does **not** `TryAdd` an in-memory store as a
convenience. Registering the core without a store **throws at registration time**, naming
`AddThemiaChallengesDapper()` / `AddThemiaChallengesEFCore()`.

A host that adopts this package specifically to get persistence, and silently receives an in-memory store
instead, is worse off than before — it believes the restart-drops-challenges problem is fixed when it is
not. An in-memory store, if it ships at all, is an explicitly named opt-in
(`AddThemiaChallengesInMemoryStore()`) documented as single-instance and non-durable. Never a default.

Schema is owned by FluentMigrator, one migration with `IfDatabase(...)` per engine, following
`MessagingSchemaMigration` — including its lesson: **a single literal table name on every engine**
(`challenges`), never schema-qualified, because FluentMigrator drops `InSchema(...)` on MySQL.

Unique index on `(tenant_id, key, purpose)` for the live challenge; separate index on the token hash for
`VerifyByTokenAsync`.

### ⚠️ ezy-assets cannot adopt v1, and that is a known gap

ezy-assets takes **no Themia data peer at all** — not Dapper, not EF (`Directory.Packages.props:43`,
deliberate, confirmed by them on coord #0050). Both v1 stores require a peer, so
`AddThemiaChallenges()` will throw on their host.

This is the same wall the Messaging inbox hit, recorded here rather than discovered by them at build
time. #0054 states plainly that ezy-assets will need this feature. Options, none chosen yet and none
blocking v1:

- they adopt a data peer (their call, not ours);
- a store that takes a `DbConnection` directly, with no peer dependency;
- a Redis-backed store, which suits short-TTL challenges well.

The decision belongs on #0056 with ezy-assets in the thread — not in this spec.

## Boundaries — what this package does NOT do

- **Delivery.** `Themia.Notifications` already ships `ISmsSender` / `IEmailSender`. This core hands the
  caller a secret and never sends anything; it defines no competing abstraction.
  - **⚠️ Adopters must check `NotificationResult.NotConfigured`** before treating a send as done. The
    logger stubs report `NotConfigured` (not success) when no provider is wired — coord #0057. An OTP
    "sent" through an unconfigured stub is an authentication outage with no signal.
- **Users.** The core does not know what a user is. `FindByPhoneAsync` and identifier resolution are
  coord #0054 item (1), in `Themia.Modules.Identity`.
- **Token issuance.** "Verified challenge → access + refresh tokens" is an Identity concern; see below.
- **Whether an unknown key should appear to succeed.** The core cannot decide this — it does not know
  what "registered" means. It issues a challenge for any key, so an adopter can implement
  always-appear-to-succeed without the framework fighting them. Propertiezy raised this on #0054 and it
  is still theirs to answer.
- **Tenant resolution.** The caller passes `TenantId` explicitly rather than the core reading an ambient
  tenant — a neutral core must not depend on `Themia.MultiTenancy`. Hosts that have an ambient tenant
  pass it in one line.

## Identity integration — separate spec, boundary recorded here

Propertiezy asked for `LoginWithOtpAsync(phone, code) -> LoginResult`. That returns Identity's
`LoginResult` and mints Identity's `AuthTokens`, so it cannot live in a package that does not know what a
user is.

**Proposed shape, to be specced separately:** `Themia.Modules.Identity` references `Themia.Challenges`
(neutral, no project dependencies of its own) and adds the flow composing `IChallengeService` +
`FindByPhoneAsync` + its existing token issuance. The core stays neutral; Identity is merely a caller.

The alternative — the adopter writing that glue — would require Identity to expose enough of its token
pipeline publicly that every consumer re-derives the security-critical part. That is the same objection
propertiezy made about identifier resolution on #0054.

## Testing

- Every security requirement above gets a test that fails if the requirement is removed. A rate limiter
  with no test asserting the *second* request is refused is decorative.
- **Both rate-limit layers tested separately**, including the purpose-cycling bypass: N requests across N
  distinct purposes must hit the per-key ceiling even though no per-purpose limit was reached.
- **Cross-tenant isolation:** a code issued for `(tenant A, +66…, login)` must NOT verify under tenant B,
  and tenant A exhausting its limit must not affect tenant B.
- **Concurrency:** two simultaneous `VerifyAsync` calls with the correct secret — exactly one wins. The
  requirement most likely to be quietly broken by a later refactor to read-then-write.
- **Hashing:** the plaintext secret does not appear in the persisted row.
- **Integration tests against real PostgreSQL / MySQL / SQL Server** via Testcontainers, matching the
  Messaging module. Atomic consume is engine-specific and cannot be verified in memory.
- **No test may log a secret.** Codes and tokens are credentials.

## Out of scope for v1

- The opaque-token generator (API present, generator unimplemented — no adopter asked).
- Any SMS or email provider implementation.
- The Identity integration flow (separate spec).
- A peer-free store for ezy-assets (see the gap above — decision belongs on #0056).
