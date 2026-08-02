# Themia messaging identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the two independently-configured service-identity values (`MessagingModuleOptions.Origin` and `VerificationOptions.Origin`) with one `MessagingIdentity` registered once in the neutral core, so the outbound stamp and the loop-guard comparison can never disagree.

**Architecture:** A new `MessagingIdentity` type in `Themia.Messaging` (the neutral core both sides already reach) holds the origin. `MessageOutboxStore` and `HmacVerificationFilter` both resolve it. Both existing `Origin` properties are deleted. `Themia.Messaging.AspNetCore` gains one project reference to `Themia.Messaging`.

**Tech Stack:** .NET 10, xUnit, `Microsoft.Extensions.DependencyInjection`, `Microsoft.CodeAnalysis.PublicApiAnalyzers`.

**Spec:** `docs/superpowers/specs/2026-08-02-themia-messaging-identity-design.md`

## Global Constraints

- **Never modify `CLAUDE.md`.** It carries an unrelated pre-existing modification. Never `git add` it.
- **Never log a secret, a signature, or a payload.** Message payloads carry PII.
- `TreatWarningsAsErrors=true` and `GenerateDocumentationFile=true` are on. Every new public member needs an XML doc comment or the build fails.
- Every new public member must be added to that package's `PublicAPI.Unshipped.txt` or the build fails with RS0016. Every removed public member must be deleted from it or the build fails with RS0017.
- **Do not touch `src/neutral/Themia.Messaging.AspNetCore/LoopGuard.cs` or `tests/Themia.Messaging.AspNetCore.Tests/LoopGuardTests.cs`.** The spec section "`LoopGuard` is not touched" explains why: the empty-origin branch is redundant, not dead, and removing it changes no behaviour.
- **No test is deleted in this plan.** Tests are retargeted, never removed.
- `System.Text.Json` only — never `Newtonsoft.Json`.
- Commit subjects: `<type>: <subject>`, imperative, under 72 chars. **Never** add `Co-authored-by:` or "Generated with" trailers.
- Run `dotnet build Themia.sln` from `Packages/themia/`. Warnings are failures.

---

### Task 1: `MessagingIdentity` in the neutral core

**Files:**
- Create: `src/neutral/Themia.Messaging/MessagingIdentity.cs`
- Create: `src/neutral/Themia.Messaging/DependencyInjection/MessagingIdentityServiceCollectionExtensions.cs`
- Create: `tests/Themia.Messaging.Tests/MessagingIdentityTests.cs`
- Modify: `src/neutral/Themia.Messaging/PublicAPI.Unshipped.txt`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `Themia.Messaging.MessagingIdentity` with `public string Origin { get; }` and `public MessagingIdentity(string origin)`. `Themia.Messaging.DependencyInjection.MessagingIdentityServiceCollectionExtensions.AddThemiaMessagingIdentity(this IServiceCollection services, string origin) -> IServiceCollection`. Tasks 2 and 3 both resolve `MessagingIdentity` from DI and both check for its registration by `ServiceType`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Themia.Messaging.Tests/MessagingIdentityTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

using Themia.Messaging.DependencyInjection;

using Xunit;

namespace Themia.Messaging.Tests;

public class MessagingIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ShouldThrow_WhenOriginIsBlank(string? origin)
    {
        Assert.ThrowsAny<ArgumentException>(() => new MessagingIdentity(origin!));
    }

    [Fact]
    public void Constructor_ShouldExposeOrigin()
    {
        Assert.Equal("propertiezy", new MessagingIdentity("propertiezy").Origin);
    }

    [Fact]
    public void AddThemiaMessagingIdentity_ShouldRegisterTheIdentity()
    {
        var services = new ServiceCollection();

        services.AddThemiaMessagingIdentity("propertiezy");

        var identity = services.BuildServiceProvider().GetRequiredService<MessagingIdentity>();
        Assert.Equal("propertiezy", identity.Origin);
    }

    [Fact]
    public void AddThemiaMessagingIdentity_ShouldThrow_WhenCalledASecondTime()
    {
        var services = new ServiceCollection();
        services.AddThemiaMessagingIdentity("propertiezy");

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddThemiaMessagingIdentity("ezy-assets"));

        Assert.Contains("AddThemiaMessagingIdentity", ex.Message, StringComparison.Ordinal);
    }

    // The instance-scan alternative would miss this: a factory registration has a null
    // ImplementationInstance, so a second descriptor would be appended and DI would resolve the
    // LAST one — two identities coexisting with the later silently winning, which is the exact
    // drift this type exists to remove.
    [Fact]
    public void AddThemiaMessagingIdentity_ShouldThrow_WhenIdentityWasRegisteredViaAFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new MessagingIdentity("registered-directly"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddThemiaMessagingIdentity("propertiezy"));

        Assert.Contains("AddThemiaMessagingIdentity", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddThemiaMessagingIdentity_ShouldThrow_WhenOriginIsBlank(string? origin)
    {
        var services = new ServiceCollection();

        Assert.ThrowsAny<ArgumentException>(() => services.AddThemiaMessagingIdentity(origin!));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~MessagingIdentityTests"`
Expected: FAIL — `MessagingIdentity` does not exist (compile error).

- [ ] **Step 3: Write `MessagingIdentity`**

Create `src/neutral/Themia.Messaging/MessagingIdentity.cs`:

```csharp
namespace Themia.Messaging;

/// <summary>
/// This service's identity on the messaging fabric: stamped on every message it originates, and
/// compared by the receiving loop guard against the inbound <c>{prefix}Origin</c> header.
/// </summary>
/// <remarks>
/// Registered once, and read by both halves of the system — the outbox store that stamps outbound
/// messages and the verification filter that detects loopback. Holding it in one place is what makes
/// the two agree by construction: when the stamp and the comparison came from separate configuration
/// values, drift between them silently disabled loop protection, with no exception and no log.
/// </remarks>
public sealed class MessagingIdentity
{
    /// <summary>Creates the identity.</summary>
    /// <param name="origin">
    /// This service's origin identifier, e.g. <c>propertiezy</c>. Must be unique across every service
    /// on the fabric: two services sharing an origin makes each one's messages look like the other's
    /// loopback.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is null, empty or whitespace.</exception>
    public MessagingIdentity(string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        Origin = origin;
    }

    /// <summary>This service's origin identifier.</summary>
    public string Origin { get; }
}
```

- [ ] **Step 4: Write the DI extension**

Create `src/neutral/Themia.Messaging/DependencyInjection/MessagingIdentityServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Themia.Messaging.DependencyInjection;

/// <summary>DI entry point for this service's messaging identity.</summary>
public static class MessagingIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers this service's <see cref="MessagingIdentity"/>. Call this BEFORE
    /// <c>AddThemiaMessagingModule</c> and <c>AddThemiaMessagingVerification</c>, both of which
    /// require it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="origin">This service's origin identifier.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// A <see cref="MessagingIdentity"/> is already registered. A second registration would append a
    /// descriptor rather than replace one, leaving two identities in the container with the later
    /// silently winning — which is the drift this type exists to remove.
    /// </exception>
    public static IServiceCollection AddThemiaMessagingIdentity(this IServiceCollection services, string origin)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        // ServiceType, not ImplementationInstance: a factory registration carries a null instance, so
        // an instance-scan would miss it and append a second descriptor. Mirrors the same check in
        // AddThemiaMessagingHmac.
        if (services.Any(d => d.ServiceType == typeof(MessagingIdentity)))
        {
            throw new InvalidOperationException(
                "A MessagingIdentity is already registered. This service has exactly one identity, and a "
                + "second registration would leave two in the container with the later silently winning. "
                + "Call AddThemiaMessagingIdentity(...) once, in one place.");
        }

        services.AddSingleton(new MessagingIdentity(origin));
        return services;
    }
}
```

- [ ] **Step 5: Declare the public API**

Add to `src/neutral/Themia.Messaging/PublicAPI.Unshipped.txt`, keeping the file sorted:

```
Themia.Messaging.DependencyInjection.MessagingIdentityServiceCollectionExtensions
Themia.Messaging.MessagingIdentity
Themia.Messaging.MessagingIdentity.MessagingIdentity(string! origin) -> void
Themia.Messaging.MessagingIdentity.Origin.get -> string!
static Themia.Messaging.DependencyInjection.MessagingIdentityServiceCollectionExtensions.AddThemiaMessagingIdentity(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, string! origin) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~MessagingIdentityTests"`
Expected: PASS, 9 tests.

Then: `dotnet build Themia.sln --no-incremental`
Expected: 0 warnings, 0 errors. A `RS0016` here means a PublicAPI line is missing or misspelled.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Messaging tests/Themia.Messaging.Tests
git commit -m "feat(messaging): add MessagingIdentity to the neutral core"
```

---

### Task 2: Module side reads the identity

**Files:**
- Modify: `src/modules/Themia.Modules.Messaging/MessagingModuleOptions.cs`
- Modify: `src/modules/Themia.Modules.Messaging/Stores/MessageOutboxStore.cs:13-16` and `:40`
- Modify: `src/modules/Themia.Modules.Messaging/DependencyInjection/MessagingServiceCollectionExtensions.cs`
- Modify: `src/modules/Themia.Modules.Messaging/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Modules.Messaging.Tests/Stores/MessageOutboxStoreTests.cs`
- Test: `tests/Themia.Modules.Messaging.Tests/MessagingModuleOptionsTests.cs`
- Test: `tests/Themia.Modules.Messaging.Tests/MessagingModuleTests.cs`
- Test: `tests/Themia.Modules.Messaging.Tests/DependencyInjection/AddThemiaMessagingModuleTests.cs`
- Test: `tests/Themia.Modules.Messaging.Tests/DependencyInjection/MessagingRegistrationOrderingTests.cs`

**Interfaces:**
- Consumes: `MessagingIdentity` and `AddThemiaMessagingIdentity` from Task 1.
- Produces: `MessagingModuleOptions` no longer has `Origin`. `AddThemiaMessagingModule` now throws when no `MessagingIdentity` is registered. Task 4's integration tests must call `AddThemiaMessagingIdentity` before `AddThemiaMessagingModule`.

- [ ] **Step 1: Retarget the store tests**

In `tests/Themia.Modules.Messaging.Tests/Stores/MessageOutboxStoreTests.cs`, the helper at line 16 currently builds options carrying the origin. Change the store construction so the origin comes from a `MessagingIdentity` instead. The helper becomes:

```csharp
private static MessagingModuleOptions Options() =>
    new() { ConnectionStringName = "Default" };
```

and every `new MessageOutboxStore(repository, time, Options(origin))` call site becomes:

```csharp
new MessageOutboxStore(repository, time, Options(), new MessagingIdentity(origin))
```

Add `using Themia.Messaging;` to the file. The two behaviour tests keep their exact assertions —
`EnqueueAsync_ShouldUseConfiguredOrigin_WhenEnvelopeOriginNotSet` still expects
`"configured-origin"`, and `EnqueueAsync_ShouldPreferEnvelopeOrigin_OverConfiguredOrigin` still
expects `"envelope-origin"`. Only where the fallback origin comes from changes.

- [ ] **Step 2: Retarget the options and module tests**

In `MessagingModuleOptionsTests.cs`: delete the `Origin = "svc-orders"` initialiser from every
`new MessagingModuleOptions { … }`, and **retarget** (do not delete) the
`Validate_ShouldThrow_WhenOriginIsMissing` theory onto the remaining required string:

```csharp
    // ConnectionStringName is the last required string on these options: Origin moved to
    // MessagingIdentity, which validates it in its own constructor.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_ShouldThrow_WhenConnectionStringNameIsMissing(string? name)
    {
        var options = new MessagingModuleOptions { ConnectionStringName = name! };

        var ex = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Equal("ConnectionStringName", ex.ParamName);
    }
```

If a `Validate_ShouldThrow_WhenConnectionStringNameIsMissing` test already exists at line 25, merge
rather than duplicate — keep one theory covering all three blank inputs.

In `MessagingModuleTests.cs`: `Constructor_ShouldSucceed_WhenOriginIsSet` becomes
`Constructor_ShouldSucceed_WithValidOptions` using `new MessagingModuleOptions()`, and
`Constructor_ShouldThrow_WhenOriginIsBlank` becomes
`Constructor_ShouldThrow_WhenConnectionStringNameIsBlank` asserting
`ex.ParamName == "ConnectionStringName"`.

- [ ] **Step 3: Add the registration-guard test**

In `tests/Themia.Modules.Messaging.Tests/DependencyInjection/MessagingRegistrationOrderingTests.cs`,
add:

```csharp
    [Fact]
    public void AddThemiaMessagingModule_ShouldThrow_WhenNoMessagingIdentityIsRegistered()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaMessagingModule());

        Assert.Contains("AddThemiaMessagingIdentity", ex.Message, StringComparison.Ordinal);
    }
```

and update the two existing tests at lines 38 and 48 — they call
`AddThemiaMessagingModule(o => o.Origin = "test-origin")`, which no longer compiles. Each needs
`services.AddThemiaMessagingIdentity("test-origin");` first, then `services.AddThemiaMessagingModule()`.
Same for the three call sites in `AddThemiaMessagingModuleTests.cs` (lines 21, 61, 86 — line 86 sets
other options alongside `Origin`, so keep the callback and drop only the `Origin` line).

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Modules.Messaging.Tests"`
Expected: FAIL to compile — `MessagingModuleOptions.Origin` still exists but `MessageOutboxStore`
takes no identity parameter.

- [ ] **Step 5: Remove `Origin` from the options**

In `src/modules/Themia.Modules.Messaging/MessagingModuleOptions.cs`, delete the `Origin` property
(lines 9-13) and its `Validate()` check (lines 48-49).

- [ ] **Step 6: Inject the identity into the store**

In `src/modules/Themia.Modules.Messaging/Stores/MessageOutboxStore.cs`, add the parameter and use it:

```csharp
internal sealed class MessageOutboxStore(
    IRepository<MessageOutboxEntry, Guid> repository,
    TimeProvider time,
    MessagingModuleOptions options,
    MessagingIdentity identity) : IMessageOutboxStore
```

and at line 40:

```csharp
            // The envelope's Origin wins when set; otherwise fall back to this service's identity.
            // MessagingIdentity's constructor already guarantees Origin is non-blank.
            Origin = string.IsNullOrWhiteSpace(message.Origin) ? identity.Origin : message.Origin,
```

Add `using Themia.Messaging;` if the file does not already have it. If `options` becomes unused after
this edit, remove the parameter — do not leave an unused dependency.

- [ ] **Step 7: Guard the module registration**

In `MessagingServiceCollectionExtensions.AddThemiaMessagingModule`, immediately after
`ArgumentNullException.ThrowIfNull(services);`:

```csharp
        if (services.All(d => d.ServiceType != typeof(MessagingIdentity)))
        {
            throw new InvalidOperationException(
                "AddThemiaMessagingModule requires AddThemiaMessagingIdentity(...) to already be registered: "
                + "MessageOutboxStore stamps this service's identity on every message it originates. Call "
                + "AddThemiaMessagingIdentity(...) BEFORE calling AddThemiaMessagingModule.");
        }
```

Add `using Themia.Messaging;` to the file. Update the method's `<exception>` XML doc to mention the
new case, and add a sentence to the `<summary>` naming `AddThemiaMessagingIdentity` as a prerequisite.

- [ ] **Step 8: Update the public API surface**

Remove from `src/modules/Themia.Modules.Messaging/PublicAPI.Unshipped.txt`:

```
Themia.Modules.Messaging.MessagingModuleOptions.Origin.get -> string!
Themia.Modules.Messaging.MessagingModuleOptions.Origin.set -> void
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Modules.Messaging.Tests"`
Expected: PASS.

Then: `dotnet build Themia.sln --no-incremental`
Expected: 0 warnings, 0 errors. `RS0017` here means a removed member is still listed in PublicAPI.

- [ ] **Step 10: Commit**

```bash
git add src/modules/Themia.Modules.Messaging tests/Themia.Modules.Messaging.Tests
git commit -m "refactor(messaging): read the outbox origin from MessagingIdentity"
```

---

### Task 3: Verification side reads the identity

**Files:**
- Modify: `src/neutral/Themia.Messaging.AspNetCore/Themia.Messaging.AspNetCore.csproj`
- Modify: `src/neutral/Themia.Messaging.AspNetCore/VerificationOptions.cs`
- Modify: `src/neutral/Themia.Messaging.AspNetCore/HmacVerificationFilter.cs:24-60` and `:120`
- Modify: `src/neutral/Themia.Messaging.AspNetCore/DependencyInjection/AspNetCoreServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Messaging.AspNetCore/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Messaging.AspNetCore.Tests/HmacVerificationFilterTests.cs`
- Test: `tests/Themia.Messaging.AspNetCore.Tests/RoundTripTests.cs:174-206`
- Test: `tests/Themia.Messaging.AspNetCore.Tests/AddThemiaMessagingVerificationTests.cs`

**Interfaces:**
- Consumes: `MessagingIdentity` and `AddThemiaMessagingIdentity` from Task 1.
- Produces: `VerificationOptions` no longer has `Origin`. `HmacVerificationFilter`'s constructor gains a `MessagingIdentity` parameter, after `verificationOptions`. `AddThemiaMessagingVerification` now throws when no identity is registered.

**DO NOT** modify `LoopGuard.cs` or `LoopGuardTests.cs` — see Global Constraints.

- [ ] **Step 1: Retarget the filter tests**

In `HmacVerificationFilterTests.cs`, four tests build `new VerificationOptions { Origin = "self" }`
(lines 246, 261, 276, 294). The origin moves to the filter's new parameter: each becomes
`new VerificationOptions()` plus a `new MessagingIdentity("self")` passed to the filter constructor.
Find the shared filter-construction helper in that file and thread the identity through it rather
than editing four call sites, if one exists.

Every assertion stays exactly as-is. In particular
`InvokeAsync_ShouldReject401_NotShortCircuitTo200_WhenOriginMatchesButSignatureIsInvalid` (line 288)
is the ordering test proving the loop guard runs *after* verification — it must keep passing
unchanged, because an attacker forging `Origin` to claim to be this service must still be rejected.

- [ ] **Step 2: Retarget the round-trip receiver**

In `RoundTripTests.cs`, the `Receiver.StartAsync` helper takes `string? ownOrigin = null` (line 174)
and applies it at line 202-205 via `o.Origin = ownOrigin`. Replace that with an identity
registration in the same service-configuration block:

```csharp
                        services.AddThemiaMessagingIdentity(ownOrigin ?? "receiver-default-origin");
```

placed **before** `AddThemiaMessagingVerification(...)`, and delete the `if (ownOrigin is not null)`
block that set `o.Origin`. Case 5 (line 124) still passes `ownOrigin: "receiver-service"` and still
asserts 200-without-endpoint; the other four cases now get a non-matching default origin, which is
the same behaviour they had when `Origin` was null.

- [ ] **Step 3: Add the registration-guard test**

In `AddThemiaMessagingVerificationTests.cs`:

```csharp
    [Fact]
    public void AddThemiaMessagingVerification_ShouldThrow_WhenNoMessagingIdentityIsRegistered()
    {
        var services = new ServiceCollection();
        services.AddThemiaMessagingHmac(o => o.AddPeer("peer", p =>
        {
            p.SignWith("k", "s");
            p.Accept("k", "s");
        }));

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaMessagingVerification());

        Assert.Contains("AddThemiaMessagingIdentity", ex.Message, StringComparison.Ordinal);
    }
```

The two existing startup-warning tests (lines 40, 65) build a host that calls
`AddThemiaMessagingVerification` — each needs `services.AddThemiaMessagingIdentity("test-origin");`
added before it.

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Messaging.AspNetCore.Tests"`
Expected: FAIL to compile — `VerificationOptions.Origin` still exists, the filter takes no identity.

- [ ] **Step 5: Add the project reference**

In `src/neutral/Themia.Messaging.AspNetCore/Themia.Messaging.AspNetCore.csproj`, add to the existing
`ProjectReference` `ItemGroup`:

```xml
    <ProjectReference Include="..\Themia.Messaging\Themia.Messaging.csproj" />
```

- [ ] **Step 6: Remove `Origin` from `VerificationOptions`**

Delete the `Origin` property (lines 11-17) from
`src/neutral/Themia.Messaging.AspNetCore/VerificationOptions.cs`. Update the class `<summary>` — it
currently reads "this service's own identity for the loop guard, and which peers are known to lack
loop protection"; the first clause is no longer true. The class keeps `MarkBiDirectional` and
`BiDirectionalPeers`, which `LoopGuardStartupWarnings` consumes.

- [ ] **Step 7: Inject the identity into the filter**

In `HmacVerificationFilter.cs`, add a `MessagingIdentity identity` parameter after
`verificationOptions`, with its null check, field, XML doc, and assignment matching the existing
five. Add `using Themia.Messaging;`. At line 120:

```csharp
        if (LoopGuard.IsLoopback(headers, peer.HeaderNames, identity.Origin))
```

If `verificationOptions` becomes unused in the filter after this edit, remove the parameter and its
field — but check first: it may still be read elsewhere in `InvokeAsync`.

- [ ] **Step 8: Guard the verification registration**

In `AspNetCoreServiceCollectionExtensions.AddThemiaMessagingVerification`, after the existing
`HmacOptions` check:

```csharp
        if (services.All(d => d.ServiceType != typeof(MessagingIdentity)))
        {
            throw new InvalidOperationException(
                "AddThemiaMessagingVerification requires AddThemiaMessagingIdentity(...) to already be "
                + "registered: HmacVerificationFilter compares this service's identity against the inbound "
                + "Origin header to detect a message that has looped back. Call AddThemiaMessagingIdentity(...) "
                + "BEFORE calling AddThemiaMessagingVerification.");
        }
```

Add `using Themia.Messaging;`. Update the method's `<exception>` and `<remarks>` XML docs to name the
new prerequisite alongside the existing `AddThemiaMessagingHmac` one.

- [ ] **Step 9: Update the public API surface**

Remove from `src/neutral/Themia.Messaging.AspNetCore/PublicAPI.Unshipped.txt`:

```
Themia.Messaging.AspNetCore.VerificationOptions.Origin.get -> string?
Themia.Messaging.AspNetCore.VerificationOptions.Origin.set -> void
```

Amend the `HmacVerificationFilter` constructor entry to include the new parameter. Leave the
`LoopGuard.IsLoopback` entry untouched.

- [ ] **Step 10: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Messaging.AspNetCore.Tests"`
Expected: PASS, 37 tests (36 existing + 1 new).

Then: `dotnet build Themia.sln --no-incremental`
Expected: 0 warnings, 0 errors.

- [ ] **Step 11: Commit**

```bash
git add src/neutral/Themia.Messaging.AspNetCore tests/Themia.Messaging.AspNetCore.Tests
git commit -m "refactor(messaging): read the loop-guard origin from MessagingIdentity"
```

---

### Task 4: Integration tests and whole-solution green

**Files:**
- Test: `tests/Themia.Modules.Messaging.IntegrationTests/OutboxRoundTripTests.cs:217`
- Test: `tests/Themia.Modules.Messaging.IntegrationTests/InboxAdmissionTests.cs:166,189`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: a green solution.

These tests need Docker (Testcontainers). If Docker is unavailable, report that in the task report —
do not skip or delete the tests.

- [ ] **Step 1: Update the integration wiring**

`OutboxRoundTripTests.cs:217` registers options directly:

```csharp
        services.AddSingleton(new MessagingModuleOptions { ConnectionStringName = "Default", Origin = TestOrigin });
```

becomes:

```csharp
        services.AddSingleton(new MessagingModuleOptions { ConnectionStringName = "Default" });
        services.AddThemiaMessagingIdentity(TestOrigin);
```

`InboxAdmissionTests.cs:166` and `:189` call `AddThemiaMessagingModule(o => o.Origin = "test-origin")`;
each becomes `AddThemiaMessagingIdentity("test-origin")` followed by `AddThemiaMessagingModule()`.

`MessagingDialectTests.cs` uses `TestOrigin` only as a literal value written to and read from the
database (lines 154, 421) — it never touches `MessagingModuleOptions`. Leave it alone.

- [ ] **Step 2: Run the integration tests**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Modules.Messaging.IntegrationTests"`
Expected: PASS, 57 tests.

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test Themia.sln`
Expected: PASS. Any failure outside the Messaging packages means something transitive broke — report
it, do not adjust the failing test to accommodate.

- [ ] **Step 4: Clean build**

Run: `dotnet build Themia.sln --no-incremental`
Expected: 0 warnings, 0 errors.

- [ ] **Step 5: Verify no `Origin` configuration survives**

Run: `grep -rn "MessagingModuleOptions.*Origin\|VerificationOptions.*Origin" src tests --exclude-dir=obj --exclude-dir=bin`
Expected: no matches. Any hit is a missed call site.

- [ ] **Step 6: Commit**

```bash
git add tests/Themia.Modules.Messaging.IntegrationTests
git commit -m "test(messaging): wire the integration hosts through MessagingIdentity"
```

---

## Self-Review

**Spec coverage:** All ten rows of the spec's change table map to a task — rows 1-2 to Task 1, rows
3-5 to Task 2, rows 6-9 to Task 3, row 10 (PublicAPI) split across Tasks 1-3 where each package's
members change. The spec's "no test is deleted" rule is a Global Constraint. The spec's "`LoopGuard`
is not touched" and "`VerificationOptions` survives" sections are both Global Constraints or explicit
step instructions.

**Placeholder scan:** No TBD or "handle appropriately". The two conditional spots are deliberate and
name their fallback: Task 2 Step 6 and Task 3 Step 7 both say "if the parameter becomes unused,
remove it — but check first", because whether `options`/`verificationOptions` still has a reader is a
fact the implementer must verify in the file rather than guess from this plan.

**Type consistency:** `MessagingIdentity` and `AddThemiaMessagingIdentity` are spelled identically in
Tasks 1-4. The constructor parameter order for `MessageOutboxStore` (identity last) and
`HmacVerificationFilter` (identity after `verificationOptions`) is stated once in each task's
Interfaces block and matched in the code.

**Known risk:** Task 3 Step 2 is the highest-risk edit — `RoundTripTests` is the only test proving the
sending and receiving halves agree on the wire, and rewiring its receiver could mask a real break by
changing what the test exercises. Its safety net is that case 5's assertions are unchanged: matching
origin still yields 200 without the endpoint running.
