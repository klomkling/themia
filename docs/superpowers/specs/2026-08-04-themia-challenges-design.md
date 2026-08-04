# Themia.Challenges — one-time secrets, one core

**Date:** 2026-08-04
**Status:** approved (rev 4 — see "What earlier revisions got wrong")
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
Themia.Challenges              net8.0;net10.0 — the core: issue / verify / consume, rate limit,
                               attempt cap, TTL, hashing, the FluentMigrator schema, and the
                               IChallengeDialect seam
Themia.Challenges.PostgreSql   per-engine dialect
Themia.Challenges.MySql
Themia.Challenges.SqlServer
```

### Why per-engine dialects and not `.Dapper` / `.EFCore` — and why it matters more than naming

rev 3 proposed stores over the Dapper and EF data peers. That was copied from the Messaging **module**,
which is the wrong precedent: Messaging's inbox needs a peer because *admission must commit inside the
caller's transaction*, so it needs the caller's ambient connection.

**Challenges has no such requirement.** Issue and verify are standalone operations that own their
transaction. Nothing needs to enlist in a caller's unit of work.

The right precedent is `Themia.Exceptional` — a neutral core with per-engine packages that opens its own
connection (`IOutboxDialect.CreateConnection()` in Messaging shows the same seam). Verified:
`src/neutral/Themia.Exceptional/Themia.Exceptional.csproj` targets `net8.0;net10.0`, references Dapper
and FluentMigrator directly, owns `Migrations/ExceptionLogMigration.cs`, and depends on no data peer.

Three consequences, and the second is the reason this rev exists:

1. **`net8.0;net10.0` becomes real.** A data peer is net10-only; without one the neutral-core TFM policy
   applies as written.
2. **ezy-assets can adopt v1 unchanged.** rev 3 carried a section explaining that they could not, because
   both stores required a peer they deliberately do not take. That section is gone: with a dialect
   opening its own connection, the constraint never existed. It was self-inflicted by the wrong
   precedent, and it had already been escalated to them on #0056 as a question they needed to answer.
3. **Atomic consume stays engine-specific**, which it must be — the whole point of a dialect.

### Why not `Themia.Otp`

The generic operation is *"issue a secret bound to a key, verify it once, within a TTL, under a rate
limit."* Same mechanism for **phone OTP**, **email OTP**, **magic links**, **email verification**,
**password reset**, **2FA enrolment**. `Otp` would make four of those six read as a misuse of an
SMS-shaped package. Renaming is free now — no code exists.

The key is an **opaque string**. The core never parses it, never validates its shape, never decides what
it means. Email OTP is therefore the same core with a different key and a different sender — the adopter
passes the secret to `IEmailSender` instead of `ISmsSender` (both already ship in `Themia.Notifications`).

## What earlier revisions got wrong

Recorded because each error is the kind that gets re-introduced by someone reading only the final text.

1. **rev 1: the API could not express a magic link**, while claiming the core supported them. `Verify`
   took the key first, but a user clicking a link presents only a token. The ways out were to put the key
   in the URL (leaking an email or phone into history and referrers — the very threat listed) or to make
   the user retype their email (not a magic link any more). Fixed by `VerifyByTokenAsync`.
2. **rev 1: multi-tenancy cut as "speculative" on a false premise** — that keys are globally unique.
   `Themia.Modules.Identity.Abstractions/Entities/User.cs:10` declares `User : … ITenantEntity` with a
   nullable `TenantId`. Two tenants holding the same phone number is the design. Without tenant in the
   challenge identity, tenant B could verify a code issued to tenant A. Fixed in `ChallengeScope`.
3. **rev 2: rate-limit counters and challenge rows shared one table, with no retention policy at all.**
   Those two requirements are in direct conflict — see "Storage and retention". Fixed by separating them.
4. **rev 2 decided two things without noticing it was deciding them**: that a rate limit per key is an
   acceptable account-lockout vector, and that re-issuing invalidates the outstanding secret. Both are
   real tradeoffs and are now stated as choices with their consequences.
5. **rev 3 copied the wrong precedent for storage** — `.Dapper` / `.EFCore` stores over the data peers,
   taken from the Messaging *module*. Messaging's inbox needs a peer because its admission must commit
   inside the caller's transaction; Challenges has no such requirement. The cost was not cosmetic: it
   forced net10-only, and it produced a whole section declaring that ezy-assets could not adopt v1 — a
   constraint that only existed because of the wrong choice, and which had already been escalated to
   them on #0056. Fixed by following `Themia.Exceptional`: per-engine dialects that open their own
   connection.
6. **rev 3 silently dropped a section rev 2 had.** rev 3 was written by replacing the whole file, and the
   "no silent in-memory store" requirement disappeared in the rewrite — noticed only when rev 4 grepped
   for it. Restored under "Registration". Rewriting a document wholesale loses content the same way
   rewriting a file does; the guard is to diff against the previous revision, not to re-read the new one.

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

    // Returns the quota consumed by an issue whose delivery failed. See "Rate limiting", layer 2.
    // Keyed on ChallengeIssueResult.ChallengeId, and idempotent: the refund is claimed with a guarded
    // UPDATE on the challenge row (refunded_at IS NULL), and only the winner decrements. Callers here
    // are retry-prone by nature — provider delivery webhooks are redelivered — and an unguarded refund
    // is a decrement of the counter that bounds an SMS bill, so a replayed one drives it to zero.
    // The buckets to credit come from the row's own created_at: counters are fixed-width buckets keyed
    // by window start, so nothing but the issuance time identifies the ones actually charged.
    // Returns false when there was nothing to refund (already refunded, or the row was purged).
    Task<bool> RefundAsync(Guid challengeId, CancellationToken ct = default);
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

### `purpose` and `tenantId` on `VerifyByTokenAsync` are assertions, not lookup keys

The token is unique on its own; the row is found by the token's hash alone. `purpose` and `tenantId` are
then **compared against the found row**, and a mismatch returns `NotFound` — the same outcome as a token
that does not exist.

Without that comparison a token issued for `"password-reset"` could be replayed against a
`"login"` endpoint. Returning `NotFound` rather than a distinct outcome is deliberate: a caller must not
be able to probe which purpose or tenant a token belongs to.

### Purpose is the configuration unit

```csharp
services.AddThemiaChallenges(o =>
{
    o.ConfigurePurpose("login", p =>
    {
        p.Format = ChallengeFormat.Numeric(6);
        p.Ttl = TimeSpan.FromMinutes(5);
        p.MaxAttempts = 5;
        p.MaxLiveChallenges = 1;                                   // see "Re-issue policy"
        p.PerScopeWindow = (Limit: 3, Window: TimeSpan.FromMinutes(15));
    });
});
```

TTL, attempt cap, format and limits live per purpose. This is what makes "email OTP costs nothing" true
at the **policy** level and not just the mechanism level: email arrives more slowly than SMS (queueing,
greylisting) and costs almost nothing to send, so an email purpose wants a longer TTL and a looser limit
than an SMS one. Same core, different purpose config.

`purpose` also scopes a challenge so a code issued for `"login"` cannot be replayed against
`"change-phone"`.

## Two challenge shapes

| | Numeric code (OTP) | Opaque token (magic link) |
|---|---|---|
| User types it | yes — must be short | no — travels in a URL |
| Entropy | ~20 bits (6 digits) | 256 bits |
| Brute-forceable | **yes** — attempt cap is load-bearing | no |
| Forwardable | hard (short TTL, needs the code) | **trivially — forwarding the email grants login** |
| Verify path | `VerifyAsync(scope, code)` | `VerifyByTokenAsync(token, …)` |

**v1 ships the numeric format only.** `ChallengeFormat.OpaqueToken` and `VerifyByTokenAsync` are in the
API from the start — they cost almost nothing at design time and are expensive to retrofit — but the
token generator is not implemented. Calling `VerifyByTokenAsync` in v1 throws `NotSupportedException`
naming the unshipped feature; it must NOT return `NotFound`, which would read as an expired token and
send an adopter debugging their own storage.

### The prefetch trap, recorded now rather than discovered later

The most common way magic links fail in production: **email scanners follow the link before the user
does.** Outlook Safe Links, corporate antivirus and Slack unfurling all issue a `GET` on every URL in a
message. With a naive single-use `GET`, a scanner consumes the token and the real user sees "link
expired" every time — very hard to trace to its cause.

**The rule, when magic links ship:** a `GET` on the link must be idempotent and must NOT consume the
challenge — it renders a confirmation page. The `POST` behind the user's click consumes it. Scanners do
not POST. The core cannot enforce this (it never sees the HTTP verb), but this is where someone will
look for it.

## Storage and retention

**Two tables, deliberately.** rev 2 kept the rate-limit count in the challenge row and specified no
retention at all. Those requirements cannot both hold in one table:

- never purge → the table grows without bound; every login attempt anyone ever makes is a permanent row
  in a table read on every login;
- purge → the evidence the rate limiter counts from disappears, so an attacker waits for retention to
  pass and the ceiling that protects the SMS bill **resets itself**.

| Table | Holds | Lifetime |
|---|---|---|
| `challenges` | one live challenge: scope, secret hash, token hash, attempts, expiry, consumed flag | short — purge consumed/expired rows after `ChallengeRetention` (default 24h) |
| `challenge_rate_windows` | counters per `(tenant, key)` and `(tenant, key, purpose)` with their window start | purge a window only once it has fully elapsed |

A counter therefore outlives the challenges it counted, which is the point.

Schema is owned by FluentMigrator, one migration with `IfDatabase(...)` per engine, following
`MessagingSchemaMigration` — including its lesson: **a single literal table name on every engine**, never
schema-qualified, because FluentMigrator drops `InSchema(...)` on MySQL.

Indexes: unique on `(tenant_id, key, purpose)` for the live challenge; separate index on the token hash
for `VerifyByTokenAsync`; `(tenant_id, key)` on the counter table.

Retention runs as a background purge with `PurgeEnabled` / `ChallengeRetentionHours` options, mirroring
`MessagingModuleOptions`.

### Registration: a dialect is mandatory, and there is no silent in-memory fallback

```csharp
services.AddThemiaChallenges(o => { /* purposes */ });
services.AddThemiaChallengesPostgres(connectionString);   // or MySql / SqlServer
```

`AddThemiaChallenges()` registers the policy engine and **does not `TryAdd` an in-memory dialect as a
convenience**. Registering the core without a dialect **throws at registration time**, naming
`AddThemiaChallenges{Postgres|MySql|SqlServer}(...)`, in the same shape as the guards
`AddThemiaMessagingModule` and `AddThemiaMessagingVerification` already use.

A host adopting this package specifically to stop losing challenges on restart, and silently receiving an
in-memory store instead, is worse off than before: it believes the problem is fixed when it is not. That
is the same failure class as the Notifications logger stub that reported success without sending
(coord #0057).

If an in-memory dialect ever ships it is an explicitly named opt-in
(`AddThemiaChallengesInMemory()`) documented as single-instance and non-durable. Never a default, never a
`TryAdd` fallback.

### Why not `Themia.Caching`

`Themia.Caching` exists (`src/framework/Themia.Caching/`) and is the obvious home for TTL-bound data. It
is not used here because **single-use consumption must be atomic** — verify-and-consume is one operation
that exactly one concurrent caller may win, and a cache abstraction with a provider-agnostic surface
cannot promise that across its providers. The rate-limit counter has the same requirement plus
durability. Both belong in the same transactional store.

### Why not `System.Threading.RateLimiting`

.NET 8+ ships it; it is **in-process and in-memory**. Behind a load balancer each instance holds its own
counter, so the real ceiling becomes `instances × limit` and an attacker spreads requests across
instances. A limit that protects an SMS bill must be as durable and as shared as the challenges it
guards.

## Security requirements — non-negotiable

1. **Store a hash, never the secret.** `IssueAsync` returns the plaintext once; the row keeps a salted
   hash.
   **What this does and does not buy**, stated so nobody later relaxes the TTL on its strength: hashing
   prevents casual disclosure — a support engineer reading the table, a code in a query log, a
   screenshot of a DB browser. It does **not** protect a numeric code from an attacker holding the
   backup: 10⁶ candidates fall to a GPU instantly, salted or not. What makes a leaked row worthless is
   the **short TTL and single-use**. The hash is defence in depth, not the defence.
2. **Rate limit in two layers, both required.**
   - *Per scope* (`tenant + key + purpose`) — the UX limit. Configured on `PurposeOptions`.
   - *Per key across all purposes* (`tenant + key`) — the **cost ceiling**. Without it an attacker cycles
     `login` → `reset` → `verify` → `enroll` and multiplies SMS volume by the number of purposes defined,
     never touching a per-purpose limit. The per-purpose layer protects the user's experience; only this
     layer protects the invoice. Configured on `ChallengeOptions`, **not** per purpose: counters are
     bucketed by window start, so a per-purpose window would floor the same key into a different bucket
     per purpose and hand the purpose-cycling attacker a fresh ceiling for each — the exact attack the
     layer exists to stop. One window per store makes that unrepresentable.
   - *Per key across every tenant* (`key` alone) — **optional, off by default**
     (`ChallengeOptions.PerKeyGlobalWindow`). The layer above is bucketed by `(tenant, key)` so one
     tenant exhausting its ceiling cannot lock another out — correct, and kept. But the invoice and the
     victim's inbox are not partitioned by tenant, so where the tenant is attacker-influenced (a
     caller-supplied subdomain or header, above all self-serve tenant signup) the same real number can
     be charged the per-key limit once per tenant. Off by default because for tenants that come from
     configuration a global bucket only lets one tenant's traffic refuse another's. Its counter row
     carries a reserved purpose, since a platform-level challenge already occupies `(NULL, key, NULL)`.
   - Per IP is deliberately not the mechanism: the attacker rotates IPs, and the victim is the account
     owner's number and your bill.
   - **Both layers charge before they check.** The counter is incremented first and the *returned*
     post-increment value is compared against the limit; a refused issue hands both charges back. The
     obvious ordering — read the count, compare, then increment — is a read-then-act gate with nothing
     serializing it: every concurrent caller reads the same pre-increment value, so all of them pass a
     ceiling of any size. The counters stay exact either way, which is why a test that asserts on the
     counter total cannot see the defect; only a test that asserts *how many callers were issued* can.
3. **Attempt cap per challenge.** A 6-digit code with unlimited guesses is not a second factor.
4. **Single use, atomically.** Verification and consumption are one database operation, never
   read-then-write. Two concurrent verifications must not both succeed.
5. **Re-issue policy** — see below; it is a choice, not a detail.
6. **Constant-time comparison** (`CryptographicOperations.FixedTimeEquals`) on the numeric path.
7. **Short TTL**, default 5 minutes for numeric.
8. **The rate limiter and attempt cap cannot be disabled.** Values are tunable; the mechanism is not
   removable. An off switch is how it ships disabled by accident.

### The per-key ceiling is an account-lockout vector, accepted knowingly

An attacker who knows a victim's phone number can burn the per-key ceiling with a handful of requests and
**lock the victim out until the window elapses**. On a passwordless flow there is no password to fall
back to. This is inherent to per-key limiting — per-IP does not have it but does not protect the bill —
so it is accepted, with two mitigations:

- **Set the ceiling for cost, not for security.** It exists to bound an SMS invoice, so it should sit far
  above what a real user reaches. A ceiling tuned low "to be safe" makes lockout easy and buys nothing;
  brute-force is stopped by the attempt cap, not by the issue limit.
- **`RefundAsync`.** Delivery is the adopter's job, so only the adopter knows a send failed. When it does
  — including `NotificationResult.NotConfigured` — the adopter calls
  `RefundAsync(result.ChallengeId!.Value)` and the quota is returned. A message that was never sent must
  not consume the victim's allowance. The call is idempotent — a redelivered webhook refunds once.

### Re-issue policy: `MaxLiveChallenges`, default 1

Invalidating the outstanding secret on re-issue is the safe default and it creates the most common OTP
support problem there is:

> SMS is slow → user taps "resend" → **the first code dies** → the first SMS arrives first (mobile
> queues do not preserve send order) → user types the code they can see → `Incorrect`, though it was
> valid twenty seconds ago → user resends again.

Default `MaxLiveChallenges = 1` keeps the safe behaviour, and **the adopter's UI must say the previous
code stopped working** — otherwise the loop above is what users experience. Raising it to 2–3 lets a
late-arriving first code still verify; the attempt cap and both rate limits still apply across all live
challenges for the scope, so the brute-force surface does not widen with it.

The point is that this is now a decision with a stated consequence rather than a line in a list.

## Boundaries — what this package does NOT do

- **Delivery.** `Themia.Notifications` already ships `ISmsSender` / `IEmailSender`. This core hands the
  caller a secret and never sends anything; it defines no competing abstraction.
  - **⚠️ Adopters must check `NotificationResult.NotConfigured`** before treating a send as done — and
    call `RefundAsync` when it is not. The logger stubs report `NotConfigured` (not success) when no
    provider is wired (coord #0057). An OTP "sent" through an unconfigured stub is an authentication
    outage with no signal.
- **Users.** The core does not know what a user is. `FindByPhoneAsync` and identifier resolution are
  coord #0054 item (1), in `Themia.Modules.Identity`.
- **Token issuance.** "Verified challenge → access + refresh tokens" is an Identity concern; see below.
- **Whether an unknown key should appear to succeed.** The core cannot decide this — it does not know
  what "registered" means. It issues a challenge for any key, so an adopter can implement
  always-appear-to-succeed without the framework fighting them. Propertiezy raised this on #0054 and it
  is still theirs to answer.
- **Tenant resolution.** The caller passes `TenantId` explicitly rather than the core reading an ambient
  tenant — a neutral core must not depend on `Themia.MultiTenancy`.

### Both consumers can adopt v1 — no data peer required

ezy-assets takes **no Themia data peer at all** — not Dapper, not EF (`Directory.Packages.props:43`,
deliberate, confirmed by them on coord #0050). rev 3 concluded they therefore could not use this package,
and that was escalated to them on #0056 as a question they had to answer.

**The constraint was self-inflicted and is gone.** A dialect opens its own connection from a connection
string, exactly as `Themia.Exceptional` does — which ezy-assets already consumes without a peer. They
reference `Themia.Challenges` + the engine package for their database and are done.

Retract the store question on #0056 rather than leaving them to answer a question that no longer exists.

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

- Every security requirement gets a test that fails if the requirement is removed. A rate limiter with no
  test asserting the *second* request is refused is decorative.
- **Both rate-limit layers separately**, including the purpose-cycling bypass: N requests across N
  distinct purposes must hit the per-key ceiling though no per-purpose limit was reached.
- **`RefundAsync` returns quota**: ceiling reached, refund, next issue succeeds.
- **`RefundAsync` is idempotent**: refunding the same challenge three times returns one slot, not three.
- **A failed issuance does not burn quota**: when the insert throws, both charges are released.
- **Keys differing only by case do not collide**: proven per engine — it fails on MySQL and SQL Server
  without the pinned collation, and passes on PostgreSQL either way.
- **The ceiling holds under concurrency**: with the per-key ceiling set well below N, N concurrent
  `IssueAsync` calls for one key yield at most `Limit` issued results (and at least one) on every
  engine. Asserting the counter equals N is a different, weaker claim and does not substitute.
- **Retention does not reset a limit**: purge the challenge rows, then assert the per-key ceiling is
  still enforced. This is the specific failure that made rev 2's single-table design wrong.
- **Cross-tenant isolation**: a code issued for `(tenant A, +66…, login)` must not verify under tenant B,
  and tenant A exhausting its ceiling must not affect tenant B.
- **Assertion semantics**: a token verified with the wrong `purpose` returns `NotFound`, not a distinct
  outcome.
- **Concurrency**: two simultaneous `VerifyAsync` calls with the correct secret — exactly one wins. The
  requirement most likely to be quietly broken by a later refactor to read-then-write.
- **Hashing**: the plaintext secret does not appear in the persisted row.
- **Integration tests against real PostgreSQL / MySQL / SQL Server** via Testcontainers, matching the
  Messaging module. Atomic consume is engine-specific and cannot be verified in memory.
- **Nothing logs a secret — and nothing logs a key.** Codes and tokens are credentials; a key is a phone
  number or an email address, which is PII. `ChallengeScope` and `ChallengeVerifyResult` both carry the
  key, and both are easy to log whole. Log the purpose and the outcome, never the scope.

## Out of scope for v1

- The opaque-token generator (API present, generator unimplemented — no adopter asked).
- Any SMS or email provider implementation.
- The Identity integration flow (separate spec).
- A Redis or other non-relational store. The three relational dialects cover both consumers; a
  cache-shaped store would have to re-prove atomic single-use, which is the hard part.
