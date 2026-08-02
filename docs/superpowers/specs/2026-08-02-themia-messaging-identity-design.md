# Themia messaging identity — one origin, one source

**Date:** 2026-08-02
**Status:** approved
**Tracks:** coord #0050 step 4
**Supersedes:** the composition-package design of the same date (rejected — see "Why not a facade").

## Problem

This service's identity on the messaging fabric is configured **twice**, in two packages, with
nothing linking the two values:

| Where | Type | Used for |
|---|---|---|
| `Themia.Modules.Messaging` | `MessagingModuleOptions.Origin` | stamped on every enqueued message |
| `Themia.Messaging.AspNetCore` | `VerificationOptions.Origin` | what the loop guard compares inbound headers against |

Traced end to end, the two must be the same string for loop protection to work at all:

1. `MessageOutboxStore.cs:40` — enqueue stamps `options.Origin` when the envelope leaves `Origin` unset.
2. `HttpMessageDispatcher.cs:128` — the dispatcher sends it as the `{prefix}Origin` header.
3. `MessageEnvelope.Origin` (docs, lines 38–43) — a forwarding service **preserves** the originating
   system rather than restamping itself, which is what lets a message arrive back where it started.
4. `HmacVerificationFilter.cs:120` — the receiver compares that header against `verificationOptions.Origin`.

If the two values drift, step 4 compares a header carrying identity A against identity B. They never
match, the guard never fires, and a message that has looped back is accepted and reprocessed as new.
**No exception, no log, no failing test.** On a bi-directional channel that is an infinite forwarding
loop — exactly the failure the loop guard exists to prevent.

## Decision

Move the identity into the neutral core both sides already reach, and delete both copies.

```csharp
// Themia.Messaging (neutral core)
namespace Themia.Messaging;

public sealed class MessagingIdentity
{
    public MessagingIdentity(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        Origin = origin;
    }

    /// <summary>This service's identity: stamped on outbound messages, and compared by the loop guard.</summary>
    public string Origin { get; }
}
```

Registered once:

```csharp
services.AddThemiaMessagingIdentity("propertiezy");
```

There is then exactly one place in a host where the origin string is written. Drift is not "caught" —
it is unrepresentable, on every wiring path, without a new package.

### Why the neutral core can hold it

Read from the csproj files:

- `Themia.Messaging` depends only on `Microsoft.Extensions.{DependencyInjection,Hosting,Logging}.Abstractions`.
- `Themia.Modules.Messaging` → `Themia.Messaging` — already referenced.
- `Themia.Messaging.AspNetCore` → `Themia.Messaging.Hmac` only. It gains one project reference to
  `Themia.Messaging`, reaching three abstraction packages already present in every ASP.NET host.

### Why not a facade

The previous design for this step proposed a new `Themia.Modules.Messaging.AspNetCore` package
wrapping the seven registration calls, with `origin` as a required argument fed to both options
objects. It was rejected on scrutiny: it made drift unrepresentable **only for adopters who opted
into the facade**, while the same spec preserved the seven existing calls as "public, supported" —
leaving the defect fully live on the path it promised to keep. A new NuGet package, PublicAPI
surface, and test project, to fix a config bug for some callers.

With the identity unified, the facade's remaining value is line count: nine registration calls become
five, with every correctness guard already in place. That does not carry a package. **The facade is
dropped.** It stays available as a later, separate decision if adopters actually stumble on the call
sequence — nothing here blocks it.

## Changes

| # | File | Change |
|---|---|---|
| 1 | `Themia.Messaging/MessagingIdentity.cs` | **new** — the type above |
| 2 | `Themia.Messaging/DependencyInjection/MessagingIdentityServiceCollectionExtensions.cs` | **new** — `AddThemiaMessagingIdentity(string origin)` |
| 3 | `Themia.Modules.Messaging/MessagingModuleOptions.cs` | remove `Origin` and its `Validate()` check |
| 4 | `Themia.Modules.Messaging/Stores/MessageOutboxStore.cs` | inject `MessagingIdentity`; fall back to `identity.Origin` |
| 5 | `Themia.Modules.Messaging/.../MessagingServiceCollectionExtensions.cs` | `AddThemiaMessagingModule` throws when no identity is registered |
| 6 | `Themia.Messaging.AspNetCore/VerificationOptions.cs` | remove `Origin`; `MarkBiDirectional` is all that remains |
| 7 | `Themia.Messaging.AspNetCore/HmacVerificationFilter.cs` | inject `MessagingIdentity`; pass `identity.Origin` to the guard |
| 8 | `Themia.Messaging.AspNetCore/.../AspNetCoreServiceCollectionExtensions.cs` | `AddThemiaMessagingVerification` throws when no identity is registered |
| 9 | `Themia.Messaging.AspNetCore.csproj` | add `ProjectReference` to `Themia.Messaging` |
| 10 | three `PublicAPI.Unshipped.txt` files | add `MessagingIdentity`; remove both `Origin` members |

### `LoopGuard` is not touched

An earlier draft of this spec changed `IsLoopback`'s `ownOrigin` to non-nullable and dropped its
empty-origin early return. Both were unnecessary: that branch is **redundant, not dead**. With it
removed, `LoopGuard.cs:37-39` still returns `false` for a null or empty `ownOrigin`, because
`!string.IsNullOrEmpty(origin)` already excludes an empty header and `string.Equals("self", null)`
and `string.Equals("self", "")` are both false. `LoopGuardTests.cs:35-43` asserts exactly that and
keeps passing either way.

So `LoopGuard.cs` and `LoopGuardTests.cs` are left alone. The filter will pass an origin that is
never empty; a loose nullable annotation on a public static helper does not justify a breaking
signature edit inside this change.

### `VerificationOptions` survives

With `Origin` gone it retains only `MarkBiDirectional` and `BiDirectionalPeers`, which
`LoopGuardStartupWarnings.cs:17` enumerates to warn about channels with no loop protection. A
one-method options class still earns its place — do not simplify it away.

### `AddThemiaMessagingIdentity` semantics

Mirrors `AddThemiaMessagingHmac` exactly (`HmacServiceCollectionExtensions.cs:25`): if any descriptor
with `ServiceType == typeof(MessagingIdentity)` is already present, **throw** — whatever shape that
registration took. Otherwise register the singleton.

Two weaker rules were considered and rejected:

- **`TryAddSingleton` alone** silently discards a second, differing value.
- **Scan for the registered instance, no-op when the origin matches, throw when it differs.** This
  leaves the drift representable: `services.AddSingleton(sp => new MessagingIdentity("wrong"))`
  registers a descriptor whose `ImplementationInstance` is `null`, so an instance-scan misses it, a
  second descriptor is appended, and DI resolves the *last* one. Two identities coexist and the later
  silently wins — the exact failure this spec exists to remove. The "modular host where two modules
  each declare the same identity" case that motivated the no-op is speculative: no Themia module
  registers an identity, the adopter does.

Checking `ServiceType` rather than the instance closes the hole and matches the pattern already used
one package over, which has its own regression test
(`AddThemiaMessagingHmac_ShouldThrow_WhenHmacOptionsWasAlreadyRegisteredDirectly`).

### Registration order

`AddThemiaMessagingIdentity` has no prerequisites, so "call it first" is unambiguous. Both
`AddThemiaMessagingModule` and `AddThemiaMessagingVerification` gain a guard in the same shape as the
four that already exist — scan the collection, throw at registration time with a message naming what
to call first, rather than failing later with an opaque DI activation error.

### Behaviour change: the loop guard can no longer be disabled

`VerificationOptions.Origin` documented "leave unset (the default) to disable the loop guard — every
verified request then reaches the endpoint." That off-switch disappears: the identity is always
present, so the guard always runs.

This is deliberate. The guard fires only when a message arrives carrying **its own origin**, which
means it has returned to the service that created it. There is no configuration in which accepting
your own looped message is correct — the off-switch only ever made the loop bug reachable. A
uni-directional receiver is unaffected: inbound messages carry the *sender's* origin, so the
comparison never matches and every request reaches the endpoint exactly as before.

## Testing

New:

- `MessagingIdentity` rejects null, empty, and whitespace origins.
- `AddThemiaMessagingIdentity` called twice throws.
- `AddThemiaMessagingIdentity` throws when `MessagingIdentity` was registered directly, including via
  a factory — the case an instance-scan would miss.
- `AddThemiaMessagingModule` without an identity throws, message names `AddThemiaMessagingIdentity`.
- `AddThemiaMessagingVerification` without an identity throws, same shape.
- `MessageOutboxStore` stamps `identity.Origin` when the envelope's `Origin` is unset.

Updated (behaviour preserved, source of the origin changed): `MessagingModuleOptionsTests`,
`AddThemiaMessagingModuleTests`, `MessagingRegistrationOrderingTests`, `MessageOutboxStoreTests`,
`AddThemiaMessagingVerificationTests`, `HmacVerificationFilterTests`, `OutboxRoundTripTests`,
`InboxAdmissionTests`.

**No test is deleted.** `LoopGuardTests` is untouched — see "`LoopGuard` is not touched" above.

**`RoundTripTests` is the load-bearing one.** It drives the real dispatcher against the real filter
over a `TestServer` with nothing stubbed, and is the only test proving both halves agree on the wire.
It must pass unchanged in intent: one registered identity, signed request out, verified request in,
loop guard reached. If unifying the origin broke the agreement between the two halves, this is what
catches it.

## Out of scope

- **Release.** Nothing in Messaging is tagged or on nuget.org; every `Origin` member being removed
  lives in `PublicAPI.Unshipped.txt`, so this is free today and a breaking change the moment we ship.
  That timing is the argument for doing it now.
- **`MessageEnvelope.Origin`** stays as-is. It carries the *originating* system for a forwarded
  message, which is not this service's identity and is correctly per-message.
- **Documentation of the canonical wiring sequence.** Still worth writing; tracked separately.
