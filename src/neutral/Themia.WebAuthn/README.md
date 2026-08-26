# Themia.WebAuthn

WebAuthn/passkey ceremonies over [Fido2NetLib](https://github.com/passwordless-lib/fido2-net-lib)
(MIT), with the **single-use challenge** and the **cloned-credential check** an integration has to
supply and usually does not.

Targets `net8.0` and `net10.0`.

**Not included, on purpose:**

- **Storing credentials.** The public key, its signature counter and the user it belongs to live in
  your users table, as the TOTP secret does for `Themia.Totp`. This package holds only the in-flight
  ceremony.
- **Attestation via the FIDO metadata service.** A synced passkey cannot be meaningfully attested — the
  private key is not pinned to hardware you can identify — and requesting it puts a network call in
  every registration.
- **Account recovery, enrolment UI, and any policy about when a passkey is required.** All of those
  bind to your users table.

## The two things this adds

Fido2NetLib does the cryptography correctly. Both of the following are the relying party's job, both
are easy to omit, and **both fail with a successful sign-in** — which is why they are here.

**1. The challenge must be usable exactly once.** The library verifies a response against the options
it was issued with, but stores nothing. An integration that keeps those options anywhere reusable —
a cache without eviction, a session that is not cleared, a row that is never deleted — accepts the same
signed response twice.

**2. The signature counter must move forward.** An authenticator increments it on every assertion, so a
value that does not advance means two authenticators are answering for one credential: the key has been
extracted and copied (WebAuthn §7.2 step 21). The library reports the counter and takes no position on
it. An integration that ignores it looks entirely healthy, because the cloned sign-in succeeds too.

## Registration

```csharp
builder.Services.AddThemiaWebAuthn<RedisWebAuthnChallengeStore>(o =>
{
    o.ServerDomain = "example.com";              // registrable domain — no scheme, no port
    o.ServerName = "Ezy Assets";
    o.Origins = ["https://example.com"];
    o.ChallengeTimeout = TimeSpan.FromMinutes(5);
    o.RequireResidentKey = true;                 // what makes a credential a passkey
});
```

`ServerDomain` is a **decision, not a setting**: a credential is bound to it and cannot be used from
anywhere else, so changing it after users have registered invalidates every credential they hold.

## Enrolling a passkey

```csharp
// 1. Begin. userId must be opaque, stable and random — NOT an email or username. It is stored on
//    the authenticator, it is exposed, and it cannot be changed later without invalidating the
//    credential.
var options = await webAuthn.BeginRegistrationAsync(
    user.WebAuthnHandle,          // byte[], random, generated once per user
    userName: user.Email,         // shown in the authenticator's picker
    displayName: user.FullName,
    existingCredentialIds: await credentials.IdsForAsync(user.Id, ct),  // so one authenticator does not enrol twice
    ct);

// 2. Hand `options` to navigator.credentials.create() in the browser.

// 3. Verify what comes back.
var result = await webAuthn.CompleteRegistrationAsync(
    response,
    isCredentialIdUnique: (id, c) => credentials.IsUnusedAsync(id, c),
    ct);

if (!result.Succeeded)
{
    return Error(result.Outcome switch
    {
        WebAuthnOutcome.ChallengeNotFound => "That enrolment expired. Start again.",
        _ => "That passkey could not be verified.",
    });
}

// 4. Store it. SignCount is not optional — the clone check compares the next assertion against it.
await credentials.AddAsync(user.Id, result.CredentialId!, result.PublicKey!, result.SignCount, ct);
```

`result.IsBackedUp` tells you whether the authenticator syncs this credential to a provider. A
credential that is **not** backed up dies with the device — worth knowing before it is a user's only
one, and worth prompting a second enrolment for.

## Signing in

```csharp
// Empty list = passkey sign-in: the authenticator offers the account and the user never types an
// identifier. Pass specific ids only when you already know who is signing in.
var options = await webAuthn.BeginAuthenticationAsync([], ct);

// ...browser calls navigator.credentials.get(), posts the response back...

var stored = await credentials.FindAsync(response.RawId, ct) ?? return Reject();

var result = await webAuthn.CompleteAuthenticationAsync(response, stored, ct);

switch (result.Outcome)
{
    case WebAuthnOutcome.Valid:
        // PERSIST THE COUNTER. A caller that never updates it disables the clone check silently.
        await credentials.UpdateSignCountAsync(stored.CredentialId, result.SignCount, ct);
        return SignIn(stored.UserHandle);

    case WebAuthnOutcome.SignCounterRegressed:
        // Cryptographically valid and still not trustworthy: another authenticator is answering for
        // this credential. Refuse, and treat it as a security event rather than a failed login.
        await security.RaiseClonedCredentialAsync(stored.CredentialId, ct);
        return Reject();

    case WebAuthnOutcome.ChallengeNotFound:  // expired, unknown, or a replay
    case WebAuthnOutcome.VerificationFailed:
    default:
        return Reject();
}
```

## The challenge store

```csharp
public interface IWebAuthnChallengeStore
{
    ValueTask StoreAsync(string challengeId, string optionsJson, TimeSpan ttl, CancellationToken ct = default);
    ValueTask<string?> TryConsumeAsync(string challengeId, CancellationToken ct = default);
}
```

Required by the signature of `AddThemiaWebAuthn<TChallengeStore>` — there is no overload without it and
no default, for the same reason as `Themia.Totp`'s replay store.

**Retrieve and remove must be one operation.** Split into a read followed by a delete, two concurrent
submissions of the same response both read the options before either deletes them, and both are
admitted — which is the replay the store exists to prevent.

```csharp
// Redis: GETDEL is the whole operation
public async ValueTask<string?> TryConsumeAsync(string challengeId, CancellationToken ct)
    => await _redis.StringGetDeleteAsync($"webauthn:{challengeId}");

// SQL: DELETE ... RETURNING
// DELETE FROM webauthn_challenges WHERE id = @id AND expires_at > now() RETURNING options_json
```

It must also be **shared across instances** — a process-local store means a ceremony begun on one
instance cannot be completed on another — and entries must **expire**, since an abandoned ceremony is
dead weight after `ChallengeTimeout`.

## Passkeys or TOTP?

Both are in Themia and they are not alternatives to each other.

| | `Themia.Totp` | `Themia.WebAuthn` |
| --- | --- | --- |
| role | second factor **beside** a password | replaces the password |
| what the server stores | the **same secret** the phone holds | only the **public key** |
| your database leaks | working codes for every user | nothing usable |
| phishing | a fake site relays the code in real time | impossible — bound to the origin |
| user does | types 6 digits | one biometric touch |
| works without | anything: any authenticator app | a platform or browser that supports it |

**A passkey is already two factors** — the device (possession) plus the biometric or PIN that unlocks
it (knowledge/inherence) — which is why it replaces the password rather than joining it.

**If you are choosing one to build first, build passkeys.** They remove the phishing surface that TOTP
cannot, and they remove a step from the user's flow rather than adding one.

**Keep TOTP for what passkeys cannot do:** a user on a device with no platform authenticator, a shared
or kiosk machine, step-up verification for a high-risk action, and as a recovery path when someone
loses every device holding a passkey. A passkey deployment with no fallback locks people out, and TOTP
is a reasonable fallback to have already built.
