# Themia.Challenges — one-time secrets, one core

**Date:** 2026-08-04
**Status:** approved
**Tracks:** coord #0056 (filed as `Themia.Otp`), and item (2) of coord #0054
**Renames:** the package accepted on #0056 as `Themia.Otp` ships as **`Themia.Challenges`** — see "Why not Themia.Otp".

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

#0056 accepted the package as `Themia.Otp`. The name is wrong for what the thing does, and renaming is
free right now because no code exists.

The generic operation is *"issue a secret bound to a key, verify it once, within a TTL, under a rate
limit."* That is the same mechanism for **phone OTP**, **email OTP**, **magic links**, **email
verification**, **password reset**, and **2FA enrolment**. Calling it `Otp` would have made four of those
six read as a misuse of an SMS-shaped package.

The key is an **opaque string** — a phone number, an email address, a user id. The core never parses it,
never validates its shape, and never decides what it means.

### Email OTP costs nothing

It is the same core with a different key and a different sender. The adopter passes the code to
`IEmailSender` instead of `ISmsSender` (both already ship in `Themia.Notifications`). No new API, no
configuration switch, no branch in the core.

## Two challenge shapes, one core

`Themia.Challenges` supports two secret formats, because their threat models genuinely differ:

| | Numeric code (OTP) | Opaque token (magic link) |
|---|---|---|
| User types it | yes — must be short | no — travels in a URL |
| Entropy | ~20 bits (6 digits) | 256 bits |
| Brute-forceable | **yes** — attempt cap is load-bearing | no |
| Forwardable | hard (short TTL, needs the code) | **trivially — forwarding the email grants login** |
| Appears in | the user's screen | URL, browser history, referrer, server logs |

Supporting both from the start costs almost nothing at design time and is expensive to retrofit: the
token generator becomes a strategy, and the attempt cap becomes per-challenge configuration. Both are
decisions that are hard to add once a schema and an API have shipped.

**v1 ships the numeric generator only.** The opaque-token generator and the magic-link guidance below are
specified so the shape is right, but are not built until an adopter asks. Nobody has.

### The prefetch trap, recorded now rather than discovered later

The single most common way magic links fail in production: **email scanners follow the link before the
user does.** Outlook Safe Links, corporate antivirus, and Slack unfurling all issue a `GET` on every URL
in a message. With a naive single-use `GET`, the token is consumed by a scanner and the real user sees
"link expired" every time — a support problem that is very hard to trace back to its cause.

**The rule, when magic links ship:** a `GET` on the link must be idempotent and must NOT consume the
challenge — it renders a confirmation page. The `POST` behind the user's click consumes it. Scanners do
not POST.

This is guidance for the adopter's endpoint, not something the core can enforce — but it belongs in the
core's documentation, because the core is where someone will look.

## Public API (indicative)

```csharp
namespace Themia.Challenges;

public interface IChallengeService
{
    // Rate-limited per key. The returned secret is the ONLY time the plaintext exists —
    // the store keeps a hash.
    Task<ChallengeIssueResult> IssueAsync(string key, string purpose, CancellationToken ct = default);

    // Constant-time comparison, single-use, atomic consume.
    Task<ChallengeVerifyResult> VerifyAsync(string key, string purpose, string secret, CancellationToken ct = default);
}

public enum ChallengeIssueOutcome { Issued, RateLimited }
public enum ChallengeVerifyOutcome { Verified, Incorrect, Expired, Consumed, AttemptsExhausted, NotFound }
```

Both results are **enums with computed booleans**, never bare bools — a caller must not be able to
collapse `RateLimited` or `AttemptsExhausted` into "not verified" and treat the two the same. (`Themia`
already models outcomes this way: `DispatchOutcome`, `LoginOutcome`, `NotificationOutcome`.)

`purpose` scopes a challenge so a code issued for `"login"` cannot be replayed against
`"change-phone"`. Same key, different purpose, independent challenges and independent rate limits.

## Security requirements — non-negotiable

These are the reasons the package exists. None of them is optional or configurable off.

1. **Store a hash, never the secret.** `IssueAsync` returns the plaintext once; the row keeps a salted
   hash. A leaked database backup must not hand over live login codes. This matters for numeric codes
   too, not just tokens.
2. **Rate limit per KEY, not per IP.** An unthrottled issue endpoint is an SMS-cost amplification attack
   against whoever pays the gateway bill. Per-IP limiting does not stop it — the attacker rotates IPs and
   the victim is the account owner's phone number and your invoice.
3. **Attempt cap per challenge.** A 6-digit code with unlimited guesses is not a second factor. Exceeding
   the cap burns the challenge.
4. **Single use, atomically.** Verification and consumption are one database operation, not read-then-write.
   Two concurrent verifications must not both succeed.
5. **Invalidate on re-issue.** Requesting a new code for the same `(key, purpose)` invalidates the
   outstanding one, so an intercepted older code cannot be used later.
6. **Constant-time comparison.** `CryptographicOperations.FixedTimeEquals` over the hashes.
7. **Short TTL.** Default 5 minutes for numeric codes.
8. **The rate limiter and attempt cap cannot be disabled.** Values are tunable; the mechanism is not
   removable. An off switch is how it ships disabled by accident.

## Store: no silent in-memory default

`AddThemiaChallenges()` registers the policy engine. It does **not** `TryAdd` an in-memory store as a
convenience. Registering the core without a store **throws at registration time**, naming
`AddThemiaChallengesDapper()` / `AddThemiaChallengesEFCore()`.

A host that adopts this package specifically to get persistence, and silently receives an in-memory store
instead, is worse off than before — it believes the restart-drops-challenges problem is fixed when it is
not.

If an in-memory store ships at all it is an explicitly named opt-in (`AddThemiaChallengesInMemoryStore()`)
whose documentation states it is single-instance and non-durable. Never a default, never a fallback.

Schema is owned by FluentMigrator, one migration with `IfDatabase(...)` per engine, following
`MessagingSchemaMigration` — including its lesson: **a single literal table name on every engine**
(`challenges`), not a schema-qualified one, because FluentMigrator drops `InSchema(...)` on MySQL.

## Boundaries — what this package does NOT do

- **Delivery.** `Themia.Notifications` already ships `ISmsSender` and `IEmailSender` with
  `HttpSmsSenderBase` for wiring a provider. `Themia.Challenges` hands the caller a secret and never
  sends anything. It defines no competing abstraction.
  - **⚠️ Adopters must check `NotificationResult.NotConfigured`** before treating a send as done. The
    logger stubs report `NotConfigured` (not success) when no provider is wired — see coord #0057. An OTP
    "sent" through an unconfigured stub is an authentication outage with no signal.
- **Users.** The core does not know what a user is. `FindByPhoneAsync` and identifier resolution are
  coord #0054 item (1), in `Themia.Modules.Identity`.
- **Token issuance.** "Verified challenge → access + refresh tokens" is an Identity concern. See below.
- **Whether an unknown key should appear to succeed.** The core cannot decide this: it does not know what
  "registered" means. It will issue a challenge for any key, so an adopter can implement
  always-appear-to-succeed without the framework fighting them. Decided at the endpoint. Propertiezy
  raised this question on #0054 and it is still theirs to answer.

## Identity integration — separate spec, boundary recorded here

Propertiezy asked for `LoginWithOtpAsync(phone, code) -> LoginResult`. That returns Identity's
`LoginResult` and mints Identity's `AuthTokens`, so it cannot live in a package that does not know what a
user is.

**Proposed shape, to be specced separately:** `Themia.Modules.Identity` takes a reference to
`Themia.Challenges` (neutral, no project dependencies of its own) and adds the flow that composes
`IChallengeService` + `FindByPhoneAsync` + its existing token issuance. The core stays neutral; Identity
is merely a caller.

Recorded here because the alternative — the adopter writing that glue — would require Identity to expose
enough of its token pipeline publicly that every consumer re-derives the security-critical part. That is
the same objection propertiezy made about identifier resolution on #0054.

## Testing

- Every security requirement above gets a test that fails if the requirement is removed. A rate limiter
  with no test asserting the *second* request is refused is decorative.
- **Concurrency:** two simultaneous `VerifyAsync` calls with the correct secret — exactly one wins. This
  is the requirement most likely to be quietly broken by a later refactor to read-then-write.
- **Hashing:** a test asserting the plaintext secret does not appear in the persisted row.
- **Integration tests against real PostgreSQL / MySQL / SQL Server** via Testcontainers, matching the
  Messaging module. The atomic-consume guarantee is engine-specific and cannot be verified in memory.
- **No test may log a secret.** Codes and tokens are credentials.

## Out of scope for v1

- The opaque-token generator and magic-link flow (designed above, not built — no adopter asked).
- Any SMS or email provider implementation.
- The Identity integration flow (separate spec).
- Multi-tenancy on the challenge table. Keys are already globally unique in practice (a phone number, an
  email); adding a tenant column before anyone needs it is speculative.
