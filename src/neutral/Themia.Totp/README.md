# Themia.Totp

TOTP (RFC 6238) secret generation, `otpauth://` provisioning URIs, and verification with the
**single-use replay guard** a hand-rolled implementation omits.

Pure computation: no HTTP, no credentials, no I/O, no database. Targets `net8.0` and `net10.0`.

Pinned against RFC 6238's full Appendix B vector table for SHA-1, SHA-256 and SHA-512. Every vector
supplies the time as an **instant** and lets this package derive the step, so the step arithmetic is
under test rather than supplied.

**Not included, on purpose:**

- **Rendering the URI as a QR image** — that pulls a drawing dependency into every consumer, and the
  URI is the part that has to be right. Same call as `Themia.PromptPay`, which ships the payload and
  not the picture.
- **Storing the secret.** A TOTP secret is credential material; where it lives and how it is encrypted
  at rest is a decision only your application can make, as with the key material in
  `Themia.AspNetCore.DataProtection`.
- **Enrolment, recovery codes, the pending-second-factor session, and any policy about when 2FA is
  required.** All of those bind to a users table.

## Registration

```csharp
builder.Services.AddThemiaTotp<RedisTotpReplayStore>(o =>
{
    o.Digits = 6;                       // default
    o.Period = TimeSpan.FromSeconds(30); // default
    o.Algorithm = TotpAlgorithm.Sha1;    // default — see the authenticator-app note below
    o.VerificationWindowSteps = 1;       // default: ±30s of clock skew
});
```

The replay store is a **required type parameter**. There is no overload without it and no default
implementation — see [The replay store](#the-replay-store) for why, and what to implement.

## Enrolling a user

Four steps, and **the order is the whole point**: the secret is not switched on until the user has
proved they can produce a code from it. Enable it first and a user who mis-scans, or scans into an app
on a phone they then wipe, has locked themselves out of their own account.

```csharp
// 1. Mint a secret. Do NOT mark 2FA enabled yet.
var secret = totp.GenerateSecret();                  // base32
var uri = totp.CreateProvisioningUri(secret, issuer: "Ezy Assets", accountName: user.Email);

// Hold `secret` somewhere it survives the round trip but is not yet the user's live credential:
// a pending-enrolment row, or the session. This is yours; the package does not store it.

// 2. Show the user a page with the QR AND the key as text (see below).

// 3. The user types the six digits their app shows. Verify before committing anything.
var result = await totp.VerifyAsync(secretId: user.Id.ToString(), secret, submittedCode, ct);

if (!result.Succeeded)
{
    // Wrong code, or a replay. Do not enable 2FA. Let them try again or re-scan.
    return result.Outcome == TotpOutcome.Replayed
        ? Error("That code was already used — wait for the next one.")
        : Error("That code is not right. Check the time on your phone and try again.");
}

// 4. Only now: persist the secret against the user, encrypted at rest, and set 2FA enabled.
await users.EnableTwoFactorAsync(user.Id, protector.Protect(secret), ct);
```

### The page the user scans

Two things on it, always both:

1. **The QR code**, encoding `uri.ToString()` verbatim. Any QR library will do — this package
   deliberately does not pick one for you. Server-side, a QR encoder writing a PNG or SVG; client-side,
   a JavaScript QR renderer given the URI string. The URI is short enough for a low error-correction
   level at a small size.

2. **The secret as text**, for manual entry. Not every user can scan — a desktop authenticator, a
   camera that will not focus, a screen reader. Apps accept the base32 key typed by hand, and they
   ignore spaces, so show it grouped:

   ```csharp
   var manualKey = string.Join(" ", secret.Chunk(4).Select(c => new string(c)));
   // GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ
   ```

   `CreateProvisioningUri` already strips base32 padding (`=`) from the URI, because apps reject it in
   the `secret` parameter. Strip it from the manual key too if your secret length produces any.

### Authenticator app compatibility — read this before changing `Algorithm` or `Digits`

**Google Authenticator and Microsoft Authenticator ignore the `algorithm`, `digits` and `period`
parameters in the URI.** They assume SHA-1, 6 digits and 30 seconds regardless of what you send.

So configuring `TotpAlgorithm.Sha256` produces a URI those apps scan happily, and then every code they
generate fails verification — a failure that looks like the user typing it wrong. The defaults here are
SHA-1 / 6 / 30 for exactly that reason. Change them only when you control both ends (your own app, a
hardware token you have tested), never for consumer authenticator apps.

## Verifying at login

```csharp
var result = await totp.VerifyAsync(user.Id.ToString(), decryptedSecret, submittedCode, ct);

return result.Outcome switch
{
    TotpOutcome.Valid       => Success(),
    TotpOutcome.Replayed    => Reject("That code was already used."),   // worth alerting on
    TotpOutcome.InvalidCode => Reject("Incorrect code."),
    _ => throw new UnreachableException(),
};
```

`Replayed` is separated from `InvalidCode` deliberately: it means someone submitted a code that was
genuinely issued for this credential and already spent. A wrong code is a typo; a replayed one is worth
counting differently.

## The replay store

**This is the reason the package exists.** A TOTP code stays valid for its entire step — 30 seconds by
default, and up to 90 with a ±1-step tolerance. An implementation that only asks "does this code match
a step in the window" is self-consistently correct and still lets anyone who observes the code replay
it for the rest of that window. Every test written from the RFC's description passes without the guard.

```csharp
public interface ITotpReplayStore
{
    ValueTask<bool> TryAdvanceAsync(string secretId, long matchedStep, CancellationToken ct = default);
}
```

Three requirements, each with a wrong answer that still compiles:

**It must be monotonic — one highest step per credential, not a set of used ones.** This is the
requirement that reads as done when it is not. Storing "step S was used" stops the *same* code twice
and still admits this:

1. an observer captures the code for step **S**
2. the real user signs in at **S+1** with a fresh code
3. the observer submits the captured code — and a ±1 window still covers step S, which nothing ever
   consumed

So accept `matchedStep` only when it is **strictly greater** than the highest step already accepted for
that credential. It is also the cheaper implementation: one row per credential instead of one per step,
and no expiry to sweep.

```csharp
// SQL: one row per credential, advanced by a single conditional write
public async ValueTask<bool> TryAdvanceAsync(string secretId, long matchedStep, CancellationToken ct)
    => await _db.ExecuteAsync(
        """
        INSERT INTO totp_replay (secret_id, last_step) VALUES (@id, @step)
        ON CONFLICT (secret_id) DO UPDATE SET last_step = @step
        WHERE totp_replay.last_step < @step
        """, new { id = secretId, step = matchedStep }, ct) == 1;
```

**It must be atomic.** One compare-and-set, not a read followed by a write. Split in two, concurrent
verifications of the same code race between the check and the record and both are admitted. The SQL
above is atomic because the comparison is inside the write; on Redis, use a Lua script rather than
`GET` then `SET`.

**It must be shared across instances.** A store scoped to one process holds nothing on the second one,
so on a two-instance deployment every verification reports correct with the window wide open. That is
why no in-memory implementation is registered by default and why `AddThemiaTotp` will not compile
without a store: an implementation that appears to work with a green test suite either side is worse
than none.

`secretId` is yours to choose: a user id, a credential id, whatever identifies the credential the code
belongs to. It **must not be the secret itself** — this package never handles secrets at rest and the
key would end up in your cache.
