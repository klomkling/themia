# Themia.MultiTenancy — Typed TenantId + Claims Resolution (0.6.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let JWT/int/guid apps adopt Themia tenancy with low friction — construct/extract `TenantId` from int/long/Guid over a canonical string key, and resolve the tenant from an auth claim with no `ITenantStore` catalog required.

**Architecture:** Two additive, non-breaking slices. (1) `TenantId` (a `readonly record struct` over a validated string) gains static `From(int/long/Guid)` factories that encode to a canonical string within the existing charset, plus `AsInt32/AsInt64/AsGuid` (throwing) and `TryAsInt32/TryAsInt64/TryAsGuid` (no-throw) extractors. (2) A new `ClaimsTenantResolutionStrategy` reads `TenantResolutionContext.Claims[claimType]` and returns a **`Resolved`** result carrying a minimal `TenantInfo` built from the claim — `DefaultTenantResolver` returns a non-null `Tenant` directly, bypassing the store, so resolution works with an empty catalog. Wiring mirrors the options-driven Header strategy: a `ClaimType` option + an arg-less `UseClaimsStrategy()`.

**Tech Stack:** .NET 10 (`Themia.MultiTenancy`) + net8.0;net10.0 cross-target (`Themia.Framework.Core`), xUnit, `Microsoft.Extensions.Options`/`Logging`, PublicAPI analyzer (RS0016), central version in `Directory.Build.props`. `TreatWarningsAsErrors=true`.

**Spec:** `docs/superpowers/specs/2026-06-18-themia-multitenancy-typed-tenantid-design.md`

---

## File Structure

| File | Responsibility | Action |
|------|----------------|--------|
| `src/framework/Themia.Framework.Core/Abstractions/Tenancy/TenantId.cs` | Typed construct/extract members | Modify |
| `tests/Themia.Framework.Core.Tests/Tenancy/TenantIdTypedTests.cs` | Tests for typed `From`/`As`/`TryAs` | Create |
| `src/framework/Themia.Framework.Core/PublicAPI.Unshipped.txt` | Track new public API | Modify |
| `src/framework/Themia.MultiTenancy/Options/MultiTenancyOptions.cs` | `ClaimType` option | Modify |
| `src/framework/Themia.MultiTenancy/Strategies/ClaimsTenantResolutionStrategy.cs` | Claims-based strategy | Create |
| `tests/Themia.MultiTenancy.Tests/Strategies/ClaimsTenantResolutionStrategyTests.cs` | Strategy unit tests | Create |
| `tests/Themia.MultiTenancy.Tests/Internal/DefaultTenantResolverClaimsTests.cs` | No-catalog resolution test | Create |
| `src/framework/Themia.MultiTenancy/MultiTenancyBuilder.cs` | `UseClaimsStrategy()` wiring | Modify |
| `tests/Themia.MultiTenancy.Tests/Configuration/ServiceCollectionExtensionsTests.cs` | Wiring registration test | Modify |
| `src/framework/Themia.MultiTenancy/PublicAPI.Unshipped.txt` | Track new public API | Modify |
| `Directory.Build.props` | Version bump to 0.6.0 | Modify |

Run all commands from `Packages/themia/`.

---

## Task 1: `TenantId.From(int/long/Guid)` factories

**Files:**
- Test: `tests/Themia.Framework.Core.Tests/Tenancy/TenantIdTypedTests.cs`
- Modify: `src/framework/Themia.Framework.Core/Abstractions/Tenancy/TenantId.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Framework.Core.Tests/Tenancy/TenantIdTypedTests.cs`:

```csharp
using System;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Core.Tests.Tenancy;

public class TenantIdTypedTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(-7, "-7")]
    [InlineData(int.MaxValue, "2147483647")]
    public void FromInt_EncodesAsInvariantDecimal(int value, string expected)
    {
        Assert.Equal(expected, TenantId.From(value).Value);
    }

    [Fact]
    public void FromLong_EncodesAsInvariantDecimal()
    {
        Assert.Equal("9223372036854775807", TenantId.From(long.MaxValue).Value);
    }

    [Fact]
    public void FromGuid_EncodesAsLowercaseHyphenatedHex()
    {
        var guid = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", TenantId.From(guid).Value);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Framework.Core.Tests/Themia.Framework.Core.Tests.csproj --filter "FullyQualifiedName~TenantIdTypedTests"`
Expected: FAIL — compile error, no `TenantId.From(int)` / `From(long)` / `From(Guid)` overloads exist.

- [ ] **Step 3: Write minimal implementation**

In `src/framework/Themia.Framework.Core/Abstractions/Tenancy/TenantId.cs`, add `using System.Globalization;` at the top of the file (above the `namespace` line), then add these three factories immediately after the existing `From(string? value)` method (before the closing `}` of the struct):

```csharp
    /// <summary>
    /// Creates a tenant identifier from a 32-bit integer, encoded as its invariant decimal string.
    /// </summary>
    /// <param name="value">Tenant id as an integer.</param>
    /// <returns>A <see cref="TenantId"/> whose value is the invariant decimal encoding.</returns>
    public static TenantId From(int value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates a tenant identifier from a 64-bit integer, encoded as its invariant decimal string.
    /// </summary>
    /// <param name="value">Tenant id as a long.</param>
    /// <returns>A <see cref="TenantId"/> whose value is the invariant decimal encoding.</returns>
    public static TenantId From(long value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates a tenant identifier from a GUID, encoded as its hyphenated lowercase "D" format.
    /// </summary>
    /// <param name="value">Tenant id as a GUID.</param>
    /// <returns>A <see cref="TenantId"/> whose value is the "D"-format encoding.</returns>
    public static TenantId From(Guid value) => new(value.ToString("D"));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.Framework.Core.Tests/Themia.Framework.Core.Tests.csproj --filter "FullyQualifiedName~TenantIdTypedTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/framework/Themia.Framework.Core/Abstractions/Tenancy/TenantId.cs tests/Themia.Framework.Core.Tests/Tenancy/TenantIdTypedTests.cs
git commit -m "feat: add TenantId.From(int/long/Guid) canonical-string factories"
```

---

## Task 2: `TenantId` typed extractors (`As*` / `TryAs*`)

**Files:**
- Modify: `tests/Themia.Framework.Core.Tests/Tenancy/TenantIdTypedTests.cs`
- Modify: `src/framework/Themia.Framework.Core/Abstractions/Tenancy/TenantId.cs`

- [ ] **Step 1: Write the failing test**

Add these tests inside the existing `TenantIdTypedTests` class in `tests/Themia.Framework.Core.Tests/Tenancy/TenantIdTypedTests.cs` (before the closing `}`):

```csharp
    [Fact]
    public void FromInt_RoundTripsThroughAsInt32()
    {
        Assert.Equal(123, TenantId.From(123).AsInt32());
    }

    [Fact]
    public void FromLong_RoundTripsThroughAsInt64()
    {
        Assert.Equal(123L, TenantId.From(123L).AsInt64());
    }

    [Fact]
    public void FromGuid_RoundTripsThroughAsGuid()
    {
        var guid = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        Assert.Equal(guid, TenantId.From(guid).AsGuid());
    }

    [Fact]
    public void AsInt32_Throws_ForNonIntegerValue()
    {
        Assert.Throws<FormatException>(() => new TenantId("not-a-number").AsInt32());
    }

    [Fact]
    public void AsGuid_Throws_ForNonGuidValue()
    {
        Assert.Throws<FormatException>(() => new TenantId("acme").AsGuid());
    }

    [Fact]
    public void TryAsInt32_ReturnsTrue_ForIntegerValue()
    {
        Assert.True(new TenantId("55").TryAsInt32(out var value));
        Assert.Equal(55, value);
    }

    [Fact]
    public void TryAsInt32_ReturnsFalse_ForNonIntegerValue()
    {
        Assert.False(new TenantId("acme").TryAsInt32(out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void TryAsGuid_ReturnsFalse_ForNonGuidValue()
    {
        Assert.False(new TenantId("acme").TryAsGuid(out _));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Framework.Core.Tests/Themia.Framework.Core.Tests.csproj --filter "FullyQualifiedName~TenantIdTypedTests"`
Expected: FAIL — compile error, no `AsInt32`/`AsInt64`/`AsGuid`/`TryAs*` members exist.

- [ ] **Step 3: Write minimal implementation**

In `src/framework/Themia.Framework.Core/Abstractions/Tenancy/TenantId.cs`, add these members after the `From(Guid value)` factory added in Task 1 (before the closing `}` of the struct). `NumberStyles`/`CultureInfo` come from the `System.Globalization` using added in Task 1:

```csharp
    /// <summary>Parses the value as a 32-bit integer.</summary>
    /// <returns>The integer value.</returns>
    /// <exception cref="FormatException">Thrown when the value is not a valid Int32.</exception>
    public int AsInt32() =>
        int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new FormatException($"Tenant identifier '{Value}' is not a valid Int32.");

    /// <summary>Parses the value as a 64-bit integer.</summary>
    /// <returns>The long value.</returns>
    /// <exception cref="FormatException">Thrown when the value is not a valid Int64.</exception>
    public long AsInt64() =>
        long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new FormatException($"Tenant identifier '{Value}' is not a valid Int64.");

    /// <summary>Parses the value as a GUID (hyphenated "D" format).</summary>
    /// <returns>The GUID value.</returns>
    /// <exception cref="FormatException">Thrown when the value is not a valid "D"-format GUID.</exception>
    public Guid AsGuid() =>
        Guid.TryParseExact(Value, "D", out var result)
            ? result
            : throw new FormatException($"Tenant identifier '{Value}' is not a valid GUID.");

    /// <summary>Attempts to parse the value as a 32-bit integer.</summary>
    /// <param name="value">The parsed integer when successful; otherwise zero.</param>
    /// <returns><c>true</c> when the value is a valid Int32; otherwise <c>false</c>.</returns>
    public bool TryAsInt32(out int value) =>
        int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Attempts to parse the value as a 64-bit integer.</summary>
    /// <param name="value">The parsed long when successful; otherwise zero.</param>
    /// <returns><c>true</c> when the value is a valid Int64; otherwise <c>false</c>.</returns>
    public bool TryAsInt64(out long value) =>
        long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Attempts to parse the value as a GUID (hyphenated "D" format).</summary>
    /// <param name="value">The parsed GUID when successful; otherwise <see cref="Guid.Empty"/>.</param>
    /// <returns><c>true</c> when the value is a valid "D"-format GUID; otherwise <c>false</c>.</returns>
    public bool TryAsGuid(out Guid value) =>
        Guid.TryParseExact(Value, "D", out value);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.Framework.Core.Tests/Themia.Framework.Core.Tests.csproj --filter "FullyQualifiedName~TenantIdTypedTests"`
Expected: PASS (all `TenantIdTypedTests`).

- [ ] **Step 5: Commit**

```bash
git add src/framework/Themia.Framework.Core/Abstractions/Tenancy/TenantId.cs tests/Themia.Framework.Core.Tests/Tenancy/TenantIdTypedTests.cs
git commit -m "feat: add TenantId.As*/TryAs* typed extractors"
```

---

## Task 3: Track new Core public API

**Files:**
- Modify: `src/framework/Themia.Framework.Core/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Add the new API surface**

Replace the contents of `src/framework/Themia.Framework.Core/PublicAPI.Unshipped.txt` with (keep the `#nullable enable` first line):

```text
#nullable enable
Themia.Framework.Core.Abstractions.Tenancy.TenantId.AsGuid() -> System.Guid
Themia.Framework.Core.Abstractions.Tenancy.TenantId.AsInt32() -> int
Themia.Framework.Core.Abstractions.Tenancy.TenantId.AsInt64() -> long
Themia.Framework.Core.Abstractions.Tenancy.TenantId.TryAsGuid(out System.Guid value) -> bool
Themia.Framework.Core.Abstractions.Tenancy.TenantId.TryAsInt32(out int value) -> bool
Themia.Framework.Core.Abstractions.Tenancy.TenantId.TryAsInt64(out long value) -> bool
static Themia.Framework.Core.Abstractions.Tenancy.TenantId.From(System.Guid value) -> Themia.Framework.Core.Abstractions.Tenancy.TenantId
static Themia.Framework.Core.Abstractions.Tenancy.TenantId.From(int value) -> Themia.Framework.Core.Abstractions.Tenancy.TenantId
static Themia.Framework.Core.Abstractions.Tenancy.TenantId.From(long value) -> Themia.Framework.Core.Abstractions.Tenancy.TenantId
```

- [ ] **Step 2: Clean build to verify no RS0016 (undocumented public API) diagnostics**

Run: `dotnet build src/framework/Themia.Framework.Core/Themia.Framework.Core.csproj --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors. (If RS0016 fires, a public member is missing from the file above; if RS0017 fires, a line above doesn't match a real member — reconcile the exact signature the diagnostic prints.)

- [ ] **Step 3: Commit**

```bash
git add src/framework/Themia.Framework.Core/PublicAPI.Unshipped.txt
git commit -m "chore: track TenantId typed API in PublicAPI.Unshipped"
```

---

## Task 4: `MultiTenancyOptions.ClaimType`

**Files:**
- Modify: `tests/Themia.MultiTenancy.Tests/Configuration/MultiTenancyOptionsTests.cs`
- Modify: `src/framework/Themia.MultiTenancy/Options/MultiTenancyOptions.cs`

- [ ] **Step 1: Write the failing test**

Add this test to the existing `MultiTenancyOptionsTests` class in `tests/Themia.MultiTenancy.Tests/Configuration/MultiTenancyOptionsTests.cs` (before the closing `}`):

```csharp
    [Fact]
    public void ClaimType_DefaultsToTenantId()
    {
        var options = new MultiTenancyOptions();

        Assert.Equal("tenant_id", options.ClaimType);
    }

    [Fact]
    public void ClaimType_IsSettable()
    {
        var options = new MultiTenancyOptions { ClaimType = "tid" };

        Assert.Equal("tid", options.ClaimType);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.MultiTenancy.Tests/Themia.MultiTenancy.Tests.csproj --filter "FullyQualifiedName~MultiTenancyOptionsTests"`
Expected: FAIL — compile error, no `ClaimType` property.

- [ ] **Step 3: Write minimal implementation**

In `src/framework/Themia.MultiTenancy/Options/MultiTenancyOptions.cs`, add this property after `HeaderName` (line 11, before `PathPrefix`):

```csharp
    /// <summary>
    /// Claim type inspected for the tenant identifier by the claims strategy (default: tenant_id).
    /// </summary>
    public string ClaimType { get; set; } = "tenant_id";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.MultiTenancy.Tests/Themia.MultiTenancy.Tests.csproj --filter "FullyQualifiedName~MultiTenancyOptionsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/framework/Themia.MultiTenancy/Options/MultiTenancyOptions.cs tests/Themia.MultiTenancy.Tests/Configuration/MultiTenancyOptionsTests.cs
git commit -m "feat: add MultiTenancyOptions.ClaimType (default tenant_id)"
```

---

## Task 5: `ClaimsTenantResolutionStrategy`

**Files:**
- Test: `tests/Themia.MultiTenancy.Tests/Strategies/ClaimsTenantResolutionStrategyTests.cs`
- Create: `src/framework/Themia.MultiTenancy/Strategies/ClaimsTenantResolutionStrategy.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.MultiTenancy.Tests/Strategies/ClaimsTenantResolutionStrategyTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Strategies;

namespace Themia.MultiTenancy.Tests.Strategies;

public class ClaimsTenantResolutionStrategyTests
{
    private readonly IOptions<MultiTenancyOptions> _options = Options.Create(new MultiTenancyOptions());
    private readonly NullLogger<ClaimsTenantResolutionStrategy> _logger = NullLogger<ClaimsTenantResolutionStrategy>.Instance;

    private static TenantResolutionContext ContextWithClaims(Dictionary<string, string> claims) =>
        new(null, null, new Dictionary<string, string>(), claims);

    [Fact]
    public async Task ResolveAsync_WithDefaultClaim_ReturnsResolvedTenant()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new() { ["tenant_id"] = "acme" });

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.NotNull(result.Tenant);
        Assert.Equal("acme", result.Tenant!.Id);
        Assert.Equal("acme", result.Tenant.Identifier);
        Assert.Equal("tenant_id", result.Source);
    }

    [Fact]
    public async Task ResolveAsync_WithCustomClaimType_ReturnsResolvedTenant()
    {
        var options = Options.Create(new MultiTenancyOptions { ClaimType = "tid" });
        var strategy = new ClaimsTenantResolutionStrategy(options, _logger);
        var context = ContextWithClaims(new() { ["tid"] = "globex" });

        var result = await strategy.ResolveAsync(context);

        Assert.True(result.Success);
        Assert.Equal("globex", result.Tenant!.Identifier);
        Assert.Equal("tid", result.Source);
    }

    [Fact]
    public async Task ResolveAsync_WithoutClaim_ReturnsNotFound()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new());

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
        Assert.Null(result.Tenant);
        Assert.Equal("tenant_id", result.Source);
        Assert.Equal("Claim not present", result.Reason);
    }

    [Fact]
    public async Task ResolveAsync_WithBlankClaim_ReturnsNotFound()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new() { ["tenant_id"] = "   " });

        var result = await strategy.ResolveAsync(context);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ResolveAsync_WithCancellation_Throws()
    {
        var strategy = new ClaimsTenantResolutionStrategy(_options, _logger);
        var context = ContextWithClaims(new() { ["tenant_id"] = "acme" });
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => strategy.ResolveAsync(context, cts.Token));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.MultiTenancy.Tests/Themia.MultiTenancy.Tests.csproj --filter "FullyQualifiedName~ClaimsTenantResolutionStrategyTests"`
Expected: FAIL — compile error, `ClaimsTenantResolutionStrategy` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/framework/Themia.MultiTenancy/Strategies/ClaimsTenantResolutionStrategy.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Strategies;

/// <summary>
/// Resolves the tenant directly from an authenticated principal's claim (default claim type:
/// <c>tenant_id</c>, configurable via <see cref="MultiTenancyOptions.ClaimType"/>). The claim value
/// <em>is</em> the tenant: a successful resolution returns a fully resolved
/// <see cref="TenantResolutionResult"/> carrying a minimal <see cref="TenantInfo"/> built from the
/// claim, so <c>DefaultTenantResolver</c> returns it directly and no <see cref="ITenantStore"/>
/// catalog lookup is required.
/// </summary>
public sealed class ClaimsTenantResolutionStrategy : ITenantResolutionStrategy
{
    private readonly IOptions<MultiTenancyOptions> _options;
    private readonly ILogger<ClaimsTenantResolutionStrategy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaimsTenantResolutionStrategy"/> class.
    /// </summary>
    public ClaimsTenantResolutionStrategy(IOptions<MultiTenancyOptions> options, ILogger<ClaimsTenantResolutionStrategy> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<TenantResolutionResult> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var claimType = _options.Value.ClaimType;

        if (context.Claims.TryGetValue(claimType, out var tenantIdentifier) && !string.IsNullOrWhiteSpace(tenantIdentifier))
        {
            _logger.LogDebug("Tenant identifier {Tenant} found in claim {Claim}", tenantIdentifier, claimType);
            // The claim is the tenant: return a fully resolved minimal tenant so DefaultTenantResolver
            // returns it directly and bypasses ITenantStore (no catalog required). TenantInfo requires
            // both Id and Identifier, so both take the claim value.
            var tenant = new TenantInfo(tenantIdentifier, tenantIdentifier);
            return Task.FromResult(TenantResolutionResult.Resolved(tenant, claimType));
        }

        return Task.FromResult(TenantResolutionResult.NotFound(claimType, "Claim not present"));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.MultiTenancy.Tests/Themia.MultiTenancy.Tests.csproj --filter "FullyQualifiedName~ClaimsTenantResolutionStrategyTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/framework/Themia.MultiTenancy/Strategies/ClaimsTenantResolutionStrategy.cs tests/Themia.MultiTenancy.Tests/Strategies/ClaimsTenantResolutionStrategyTests.cs
git commit -m "feat: add ClaimsTenantResolutionStrategy (Resolved result, no catalog)"
```

---

## Task 6: No-catalog resolution test (`DefaultTenantResolver` + claims + empty store)

This task adds a test only — it proves the spec's core coord-item-4 guarantee against the *existing* `DefaultTenantResolver` plus the strategy from Task 5. `DefaultTenantResolver` is `internal`; the test assembly has `InternalsVisibleTo("Themia.MultiTenancy.Tests")`.

**Files:**
- Create: `tests/Themia.MultiTenancy.Tests/Internal/DefaultTenantResolverClaimsTests.cs`

- [ ] **Step 1: Write the test**

Create `tests/Themia.MultiTenancy.Tests/Internal/DefaultTenantResolverClaimsTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Internal;
using Themia.MultiTenancy.Stores;
using Themia.MultiTenancy.Strategies;

namespace Themia.MultiTenancy.Tests.Internal;

public class DefaultTenantResolverClaimsTests
{
    [Fact]
    public async Task ResolveAsync_WithClaimsStrategyAndEmptyStore_ResolvesTenant()
    {
        var options = Options.Create(new MultiTenancyOptions());
        var claimsStrategy = new ClaimsTenantResolutionStrategy(
            options, NullLogger<ClaimsTenantResolutionStrategy>.Instance);
        var emptyStore = new InMemoryTenantStore();
        var resolver = new DefaultTenantResolver(
            new ITenantResolutionStrategy[] { claimsStrategy },
            emptyStore,
            NullLogger<DefaultTenantResolver>.Instance);

        var context = new TenantResolutionContext(
            null, null,
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["tenant_id"] = "acme" });

        var tenant = await resolver.ResolveAsync(context);

        Assert.NotNull(tenant);
        Assert.Equal("acme", tenant!.Identifier);
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/Themia.MultiTenancy.Tests/Themia.MultiTenancy.Tests.csproj --filter "FullyQualifiedName~DefaultTenantResolverClaimsTests"`
Expected: PASS — the resolver returns the tenant although the store is empty (the `Resolved` result's non-null `Tenant` is returned directly, bypassing `FindByIdentifierAsync`).

- [ ] **Step 3: Commit**

```bash
git add tests/Themia.MultiTenancy.Tests/Internal/DefaultTenantResolverClaimsTests.cs
git commit -m "test: verify claims strategy resolves with an empty tenant store"
```

---

## Task 7: `MultiTenancyBuilder.UseClaimsStrategy()` wiring

**Files:**
- Modify: `tests/Themia.MultiTenancy.Tests/Configuration/ServiceCollectionExtensionsTests.cs`
- Modify: `src/framework/Themia.MultiTenancy/MultiTenancyBuilder.cs`

- [ ] **Step 1: Write the failing test**

Add this test to the existing `ServiceCollectionExtensionsTests` class in `tests/Themia.MultiTenancy.Tests/Configuration/ServiceCollectionExtensionsTests.cs` (before the closing `}`). It also confirms claims is opt-in — not registered by default:

```csharp
    [Fact]
    public void AddThemiaMultiTenancy_DoesNotRegisterClaimsStrategy_ByDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy();

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>();

        Assert.DoesNotContain(strategies, s => s is ClaimsTenantResolutionStrategy);
    }

    [Fact]
    public void AddThemiaMultiTenancy_WithUseClaimsStrategy_ShouldRegisterClaimsStrategy()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddThemiaMultiTenancy(configure: builder => builder.UseClaimsStrategy());

        var provider = services.BuildServiceProvider();
        var strategies = provider.GetServices<ITenantResolutionStrategy>();

        Assert.Contains(strategies, s => s is ClaimsTenantResolutionStrategy);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.MultiTenancy.Tests/Themia.MultiTenancy.Tests.csproj --filter "FullyQualifiedName~ServiceCollectionExtensionsTests"`
Expected: FAIL — compile error, no `UseClaimsStrategy` method on `MultiTenancyBuilder`.

- [ ] **Step 3: Write minimal implementation**

In `src/framework/Themia.MultiTenancy/MultiTenancyBuilder.cs`, add this method immediately after `UseDefaultStrategy()` (after line 55, before `SeedTenants`):

```csharp
    /// <summary>
    /// Uses the claims strategy (resolves the tenant from an authenticated principal's claim;
    /// claim type configured via <see cref="MultiTenancyOptions.ClaimType"/>, default tenant_id).
    /// </summary>
    public MultiTenancyBuilder UseClaimsStrategy()
    {
        return AddStrategy<ClaimsTenantResolutionStrategy>();
    }
```

(No change to the default-strategies block at lines ~199-203 — claims is opt-in.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.MultiTenancy.Tests/Themia.MultiTenancy.Tests.csproj --filter "FullyQualifiedName~ServiceCollectionExtensionsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/framework/Themia.MultiTenancy/MultiTenancyBuilder.cs tests/Themia.MultiTenancy.Tests/Configuration/ServiceCollectionExtensionsTests.cs
git commit -m "feat: add MultiTenancyBuilder.UseClaimsStrategy() (opt-in)"
```

---

## Task 8: Track new MultiTenancy public API

**Files:**
- Modify: `src/framework/Themia.MultiTenancy/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Add the new API surface**

Replace the contents of `src/framework/Themia.MultiTenancy/PublicAPI.Unshipped.txt` with (keep `#nullable enable` first):

```text
#nullable enable
Themia.MultiTenancy.MultiTenancyBuilder.UseClaimsStrategy() -> Themia.MultiTenancy.MultiTenancyBuilder!
Themia.MultiTenancy.MultiTenancyOptions.ClaimType.get -> string!
Themia.MultiTenancy.MultiTenancyOptions.ClaimType.set -> void
Themia.MultiTenancy.Strategies.ClaimsTenantResolutionStrategy
Themia.MultiTenancy.Strategies.ClaimsTenantResolutionStrategy.ClaimsTenantResolutionStrategy(Microsoft.Extensions.Options.IOptions<Themia.MultiTenancy.MultiTenancyOptions!>! options, Microsoft.Extensions.Logging.ILogger<Themia.MultiTenancy.Strategies.ClaimsTenantResolutionStrategy!>! logger) -> void
Themia.MultiTenancy.Strategies.ClaimsTenantResolutionStrategy.ResolveAsync(Themia.MultiTenancy.Abstractions.TenantResolutionContext! context, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<Themia.MultiTenancy.Abstractions.TenantResolutionResult!>!
```

- [ ] **Step 2: Clean build to verify no RS0016 diagnostics**

Run: `dotnet build src/framework/Themia.MultiTenancy/Themia.MultiTenancy.csproj --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors. (If RS0016/RS0017 fire, reconcile each line to the exact signature the diagnostic prints — e.g. the `default(...)` form of the optional `CancellationToken` parameter.)

- [ ] **Step 3: Commit**

```bash
git add src/framework/Themia.MultiTenancy/PublicAPI.Unshipped.txt
git commit -m "chore: track claims strategy + ClaimType in PublicAPI.Unshipped"
```

---

## Task 9: Version bump to 0.6.0 and full verification

**Files:**
- Modify: `Directory.Build.props`

- [ ] **Step 1: Bump the shared version**

In `Directory.Build.props`, change the version line (inside the `Label="Version"` PropertyGroup):

```xml
    <Version>0.6.0</Version>
```

(from `<Version>0.5.5</Version>`)

- [ ] **Step 2: Full clean build (all TFMs)**

Run: `dotnet build Themia.sln --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors across net8.0 + net10.0.

- [ ] **Step 3: Full test run**

Run: `dotnet test Themia.sln`
Expected: All tests pass (including the new `TenantIdTypedTests`, `ClaimsTenantResolutionStrategyTests`, `DefaultTenantResolverClaimsTests`, and the new options/wiring tests).

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props
git commit -m "chore: bump version to 0.6.0 (MultiTenancy typed TenantId + claims)"
```

---

## Self-Review

**1. Spec coverage:**
- §2 typed construct/extract → Tasks 1, 2, 3. ✓ (`From(int/long/Guid)`, `As*`, `TryAs*`, canonical encodings, FormatException on `As*` mismatch, no-throw `TryAs*`.)
- §3 `ClaimsTenantResolutionStrategy` returns `Resolved` (no catalog) → Tasks 4, 5. ✓ (reads `context.Claims[claimType]`, `TenantInfo(Id, Identifier)` both = claim, `NotFound` on absent/blank.)
- §3 options-driven wiring (`ClaimType` option + arg-less `UseClaimsStrategy()` via `AddStrategy<T>()`) → Tasks 4, 7. ✓
- §3/§6 no-catalog guarantee via `DefaultTenantResolver` bypass → Task 6. ✓
- §4 tenant+user identity (docs only, no new API) → no task required by design. ✓
- §6 testing (TenantId units, strategy units, empty-store resolution, clean build/TWAE, PublicAPI) → Tasks 1–8. ✓
- §5 out of scope (generic `TTenantId`, combined accessor, DB-per-tenant routing) → no tasks, correctly omitted. ✓
- Version 0.6.0 → Task 9. ✓

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to Task N" — every code and PublicAPI step is literal. ✓

**3. Type consistency:** `ClaimType` (Tasks 4/5/7/8) consistent; `ClaimsTenantResolutionStrategy` ctor `(IOptions<MultiTenancyOptions>, ILogger<...>)` consistent across Tasks 5/8; `TenantInfo(string Id, string Identifier, …)` positional usage matches `TenantModels.cs:15`; `TenantResolutionResult.Resolved(TenantInfo, string)` / `.NotFound(string, string?)` match the real factory signatures; `DefaultTenantResolver(IEnumerable<ITenantResolutionStrategy>, ITenantStore, ILogger<DefaultTenantResolver>)` matches `DefaultTenantResolver.cs:12`; `AddStrategy<T>()` matches `MultiTenancyBuilder.cs:27`. ✓
