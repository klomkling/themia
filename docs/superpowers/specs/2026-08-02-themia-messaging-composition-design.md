# Themia Messaging composition surface — design

**Date:** 2026-08-02
**Status:** approved
**Tracks:** coord #0050 step 4 (the `AddThemiaMessaging` DI surface)
**Supersedes nothing.** Additive over the persistence (step 2b) and HMAC transport (step 3) specs.

## Problem

Wiring Themia Messaging today takes seven registration calls across four packages, in an order
that four separate throw-guards exist to police:

```csharp
services.AddThemiaDapperCore();
services.AddThemiaDapperPostgres(cs);
services.AddThemiaMessagingPostgreSql();
services.AddThemiaMessagingModule(o => { o.Origin = "propertiezy"; o.MaxAttempts = 8; });
services.AddThemiaMessagingInbox();
services.AddThemiaMessagingHmac(o => o.AddPeer("ezy-assets", p => { … }));
services.AddThemiaMessagingHttp();
services.AddThemiaMessagingVerification(v => { v.Origin = "propertiezy"; … });
```

The ordering hazard is already handled — each guard throws at registration time with a message
naming what to call first. **Ordering is not the motivating defect.**

The motivating defect is the repeated `Origin`. `MessagingModuleOptions.Origin` is stamped on every
outbound message; `VerificationOptions.Origin` is what the loop guard compares an inbound
`{prefix}Origin` header against. They must be the same string, they live in two packages, and
nothing connects them. If they drift:

- outbound messages carry identity A,
- the loop guard compares against identity B,
- a message that loops back **matches nothing, passes the guard, and is reprocessed as new**.

No exception, no log, no failing test. On a bi-directional channel that is an infinite forwarding
loop — precisely the failure the loop guard was built to prevent. This spec makes the drift
unrepresentable.

## Decision

One new package, `Themia.Modules.Messaging.AspNetCore`, holding a composition entry point that
takes `origin` **once** and owns the registration order internally.

### Why a new package

`Themia.Modules.Messaging` cannot host it: the module deliberately knows nothing about HTTP —
`IOutboxDispatcher<TRow>` is the seam that keeps transport pluggable, and referencing
`Themia.Messaging.Http` from the module would invert exactly that dependency. `Themia.Messaging.AspNetCore`
cannot host it either: it does not reference the module, and making it do so would drag EF Core,
Dapper and FluentMigrator into a package whose entire job is one endpoint filter.

The name follows established repo precedent. The metapackage spec (2026-07-11) records that
`Themia.Modules.Identity.AspNetCore` *is* the Identity umbrella — it references all four sibling
Identity packages — and that a separate `.All` package was rejected as unnecessary. Messaging gets
the same shape.

### Deliberately outside the facade

- **The data peer** (`AddThemiaDapperCore` / EF). Must run *before* `AddThemiaMessaging` so the
  `EntityMappingRegistry` singleton exists when the module contributes its mappings. The facade
  cannot control what precedes it; the existing throw in `AddThemiaMessagingModule` remains the guard.
- **The engine dialect** (`AddThemiaMessaging{PostgreSql|MySql|SqlServer}`). Lives in three sibling
  packages the facade would have to reference *all* of, dragging Npgsql + MySqlConnector +
  Microsoft.Data.SqlClient into every adopter. Picking the engine stays one explicit line.

So the facade shortens eight calls to four, not to one. It is not primarily a brevity feature.

## Public API

`Themia.Modules.Messaging.AspNetCore`, `net10.0`, at
`src/modules/Themia.Modules.Messaging.AspNetCore/`.

References: `Themia.Modules.Messaging`, `Themia.Messaging.Hmac`, `Themia.Messaging.Http`,
`Themia.Messaging.AspNetCore`. The ASP.NET Core shared framework arrives transitively from the
first and last; it is not declared again.

```csharp
namespace Themia.Modules.Messaging.AspNetCore.DependencyInjection;

public static class ThemiaMessagingServiceCollectionExtensions
{
    public static IServiceCollection AddThemiaMessaging(
        this IServiceCollection services,
        string origin,
        Action<ThemiaMessagingBuilder> configure);
}

public sealed class ThemiaMessagingBuilder
{
    public void AddPeer(string name, Action<MessagingPeerBuilder> configure);
    public void ConfigureModule(Action<MessagingModuleOptions> configure);
    public void EnableInbox();
    public void UseHttpDispatch();
    public void ConfigureVerification(Action<VerificationOptions> configure);
}
```

Usage:

```csharp
services.AddThemiaDapperCore();
services.AddThemiaDapperPostgres(cs);
services.AddThemiaMessagingPostgreSql();

services.AddThemiaMessaging("propertiezy", m =>
{
    m.AddPeer("ezy-assets", p =>
    {
        p.BaseAddress = new Uri("https://ezy-assets.internal");
        p.HeaderPrefix = "X-Propertiezy-";
        p.SignWith("2026-07", outboundSecret);
        p.Accept("2026-07", inboundSecret);
        p.Route("ListingSnapshot", "/api/sync/listings");
    });

    m.ConfigureModule(o => o.MaxAttempts = 8);
    m.EnableInbox();
    m.UseHttpDispatch();
    m.ConfigureVerification(v => v.MarkBiDirectional("ezy-assets", sendsOriginHeader: false));
});
```

### Semantics

**`origin` is required and non-empty.** It is the one identity this service publishes under and
compares loop-guard headers against. There is no default: a placeholder origin shared by two
services causes silent cross-service dedup collisions, which is why `MessagingModuleOptions.Validate()`
already rejects an empty one.

**`origin` feeds both options objects.** The facade sets `MessagingModuleOptions.Origin` and
`VerificationOptions.Origin` from the same argument. Neither callback may change it — see below.

**Capabilities stay explicit.** `EnableInbox()`, `UseHttpDispatch()` and `ConfigureVerification(...)`
map one-to-one onto the calls they replace. A send-only worker should not get the verification
hosted service; a receive-only host should not get an HTTP dispatcher it never dispatches through.
The facade owns *ordering*, not *capability* — hiding the capability split would make the facade
shorter and the resulting host wrong.

**The module is always registered.** `AddThemiaMessagingModule` runs unconditionally: the inbox
depends on it for `MessagingModuleOptions` and the outbox dialect, and there is no configuration in
which a messaging host wants none of it. A receive-only host therefore also runs the drainer, which
polls an outbox that stays empty — the same behaviour the manual eight-call sequence produces today.
No opt-out is invented for it.

**At least one peer is required.** With no peers, `UseHttpDispatch()` registers zero named clients
and verification 401s every request. An empty registry is never a valid configuration, so it throws.

**Builder call order does not matter.** The builder accumulates; the facade replays. `UseHttpDispatch()`
written before `AddPeer(...)` still produces a correctly-ordered registration, because
`AddThemiaMessagingHttp` is not invoked until every peer is known. This is the property the package
exists to provide.

### Anti-drift guards

`ConfigureModule` receives a `MessagingModuleOptions` whose `Origin` is already set. If the callback
changes it, the facade throws, naming both values:

```
ConfigureModule set MessagingModuleOptions.Origin to 'ezy-assets', but AddThemiaMessaging was called
with origin 'propertiezy'. The origin is passed once, as the AddThemiaMessaging argument, so the
outbound stamp and the loop guard can never disagree. Remove the assignment in ConfigureModule.
```

`ConfigureVerification` gets the identical treatment on `VerificationOptions.Origin`.

Throwing beats silently overwriting: an adopter who writes the assignment believes it is
load-bearing, and quietly discarding it reintroduces the drift in the opposite direction.

### Internal registration order

1. `AddThemiaMessagingHmac(...)` — every accumulated peer, in one call (the underlying method
   refuses a second call).
2. `AddThemiaMessagingModule(...)`
3. `AddThemiaMessagingInbox()` — if `EnableInbox()` was called.
4. `AddThemiaMessagingHttp()` — if `UseHttpDispatch()` was called.
5. `AddThemiaMessagingVerification(...)` — if `ConfigureVerification(...)` was called.

Hmac first because both Http and Verification require it. Module before Inbox because Inbox
requires it.

## Testing

`tests/Themia.Modules.Messaging.AspNetCore.Tests`, `net10.0`, xUnit.

The load-bearing test is **equivalence**: the facade must emit the same service descriptors as the
hand-written eight-call sequence, projected to `(ServiceType, Lifetime, ImplementationType)` and
compared as a **sorted list, not a set** — several services are registered more than once
(`AddHostedService` twice, the named `HttpClient` machinery repeatedly), so set comparison would
pass while one path registered a drainer the other did not. Without this test the two paths drift as
either side changes, and the facade silently stops being a faithful shorthand.

| # | Test | Why |
|---|---|---|
| 1 | Facade output ≡ manual sequence output | The anti-drift net. Full opt-in configuration. |
| 2 | Both `Origin` values equal the argument | The defect this package exists for. |
| 3 | Builder calls in reverse order still register correctly | The ordering property. |
| 4 | `ConfigureModule` setting `Origin` throws, message names both values | Anti-drift guard. |
| 5 | `ConfigureVerification` setting `Origin` throws | Anti-drift guard. |
| 6 | No peers → throws | Empty registry is never valid. |
| 7 | Null/empty/whitespace `origin` → throws | No safe default. |
| 8 | Omitting `EnableInbox` → no `IInboxStore`; omitting `UseHttpDispatch` → no `IOutboxDispatcher<ClaimedMessageRow>`; omitting `ConfigureVerification` → no `VerificationOptions` | Capabilities really are opt-in. |

Tests needing the inbox register a stub `IDapperConnectionContext` plus an `EntityMappingRegistry`
singleton, matching the existing pattern in
`tests/Themia.Modules.Messaging.Tests/DependencyInjection/MessagingRegistrationOrderingTests.cs`.
No database is involved — this is registration-time behaviour only.

## Out of scope

- Any change to the seven existing `Add*` methods. They remain public, supported, and the only
  path for a host that wants a non-HTTP dispatcher. The facade is additive.
- Documentation of the canonical wiring sequence. Worth doing, tracked separately; the facade
  reduces but does not remove the need for it.
- Release. Nothing in Messaging is tagged or on nuget.org yet; this package ships with the rest.
