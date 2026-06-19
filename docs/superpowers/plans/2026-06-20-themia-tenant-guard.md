# Tenant-presence guard (0.5.7) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add reusable tenant-presence *enforcement* — reject requests reaching a handler with no usable tenant (401 unauthenticated / 403 authenticated-but-tenant-less), with an opt-out marker and a privileged-role bypass.

**Architecture:** A transport-agnostic verdict (`TenantGuard.Evaluate`) plus marker/options live in `Themia.MultiTenancy` (neutral, no Mediator/Identity deps, uses `ClaimsPrincipal`). A Mediator `IPipelineBehavior` adapter lives in a new bridge package `Themia.MultiTenancy.Mediator` that translates the verdict into `Unauthorized`/`Forbidden` exceptions (mapped to 401/403 by the existing `ProblemDetailsMiddleware`).

**Tech Stack:** .NET 10, xUnit, `Themia.Mediator` (`IPipelineBehavior`), `Themia.AspNetCore` (typed exceptions), `Microsoft.AspNetCore.App` (ClaimsPrincipal/IHttpContextAccessor), PublicAPI analyzer (RS0016), central version in `Directory.Build.props`. `TreatWarningsAsErrors=true`.

**Spec:** `docs/superpowers/specs/2026-06-20-themia-tenant-guard-design.md`

All commands run from `Packages/themia/`.

---

## File Structure

| File | Responsibility | Action |
|------|----------------|--------|
| `src/framework/Themia.MultiTenancy/Guard/TenantGuard.cs` | `TenantGuardVerdict` enum + `TenantGuard.Evaluate` (pure verdict) | Create |
| `src/framework/Themia.MultiTenancy/Guard/ISkipTenantValidation.cs` | Opt-out marker interface | Create |
| `src/framework/Themia.MultiTenancy/Options/TenantGuardOptions.cs` | `PrivilegedRoles` config | Create |
| `src/framework/Themia.MultiTenancy/PublicAPI.Unshipped.txt` | Track new core API | Modify |
| `tests/Themia.MultiTenancy.Tests/Guard/TenantGuardTests.cs` | Verdict truth-table tests | Create |
| `src/framework/Themia.MultiTenancy.Mediator/Themia.MultiTenancy.Mediator.csproj` | New bridge package | Create |
| `src/framework/Themia.MultiTenancy.Mediator/PublicAPI.Shipped.txt` / `.Unshipped.txt` | PublicAPI tracking | Create |
| `src/framework/Themia.MultiTenancy.Mediator/TenantGuardBehavior.cs` | Mediator adapter | Create |
| `src/framework/Themia.MultiTenancy.Mediator/TenantGuardServiceCollectionExtensions.cs` | `AddThemiaTenantGuard()` | Create |
| `tests/Themia.MultiTenancy.Mediator.Tests/Themia.MultiTenancy.Mediator.Tests.csproj` | New test project | Create |
| `tests/Themia.MultiTenancy.Mediator.Tests/TenantGuardBehaviorTests.cs` | Behavior tests | Create |
| `tests/Themia.MultiTenancy.Mediator.Tests/TenantGuardRegistrationTests.cs` | DI registration tests | Create |
| `Directory.Build.props` | Version bump to 0.5.7 | Modify |

---

## Task 1: Neutral core — `TenantGuard.Evaluate`, marker, options (in `Themia.MultiTenancy`)

**Files:**
- Test: `tests/Themia.MultiTenancy.Tests/Guard/TenantGuardTests.cs`
- Create: `src/framework/Themia.MultiTenancy/Guard/TenantGuard.cs`, `.../Guard/ISkipTenantValidation.cs`, `.../Options/TenantGuardOptions.cs`
- Modify: `src/framework/Themia.MultiTenancy/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.MultiTenancy.Tests/Guard/TenantGuardTests.cs`:

```csharp
using System.Security.Claims;
using Themia.MultiTenancy;
using Themia.MultiTenancy.Abstractions;
using Xunit;

namespace Themia.MultiTenancy.Tests.Guard;

public class TenantGuardTests
{
    private static readonly string[] Privileged = ["SaaSAdmin"];
    private static readonly TenantInfo Tenant = new("acme", "acme");
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private static ClaimsPrincipal Authed(params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)),
            authenticationType: "test", ClaimTypes.Name, ClaimTypes.Role));

    [Fact]
    public void Skip_Bypasses_EvenWhenUnauthenticatedAndNoTenant() =>
        Assert.Equal(TenantGuardVerdict.Allow,
            TenantGuard.Evaluate(principal: null, currentTenant: null, skipRequested: true, Privileged));

    [Fact]
    public void Unauthenticated_WhenNoPrincipal() =>
        Assert.Equal(TenantGuardVerdict.Unauthenticated,
            TenantGuard.Evaluate(null, Tenant, skipRequested: false, Privileged));

    [Fact]
    public void Unauthenticated_WhenIdentityNotAuthenticated() =>
        Assert.Equal(TenantGuardVerdict.Unauthenticated,
            TenantGuard.Evaluate(Anonymous, Tenant, false, Privileged));

    [Fact]
    public void PrivilegedRole_Bypasses_TenantCheck() =>
        Assert.Equal(TenantGuardVerdict.Allow,
            TenantGuard.Evaluate(Authed("SaaSAdmin"), currentTenant: null, false, Privileged));

    [Fact]
    public void NoTenant_WhenAuthedNonPrivilegedAndTenantNull() =>
        Assert.Equal(TenantGuardVerdict.NoTenant,
            TenantGuard.Evaluate(Authed("User"), currentTenant: null, false, Privileged));

    [Fact]
    public void Allow_WhenAuthedWithTenant() =>
        Assert.Equal(TenantGuardVerdict.Allow,
            TenantGuard.Evaluate(Authed("User"), Tenant, false, Privileged));

    [Fact]
    public void PrivilegedRole_StillRequiresAuth() =>
        Assert.Equal(TenantGuardVerdict.Unauthenticated,
            TenantGuard.Evaluate(Anonymous, currentTenant: null, false, Privileged));

    [Fact]
    public void EmptyPrivilegedRoles_NoBypass() =>
        Assert.Equal(TenantGuardVerdict.NoTenant,
            TenantGuard.Evaluate(Authed("SaaSAdmin"), currentTenant: null, false, []));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.MultiTenancy.Tests/Themia.MultiTenancy.Tests.csproj --filter "FullyQualifiedName~TenantGuardTests"`
Expected: FAIL — compile error, `TenantGuard`/`TenantGuardVerdict` do not exist.

- [ ] **Step 3: Write the marker**

Create `src/framework/Themia.MultiTenancy/Guard/ISkipTenantValidation.cs`:

```csharp
namespace Themia.MultiTenancy;

/// <summary>
/// Marker interface for request types that are exempt from the tenant guard entirely (both the
/// authentication and tenant-presence checks). Implement on commands/queries that legitimately run
/// without a tenant — login, refresh, public, or system/background operations.
/// </summary>
public interface ISkipTenantValidation;
```

- [ ] **Step 4: Write the options**

Create `src/framework/Themia.MultiTenancy/Options/TenantGuardOptions.cs`:

```csharp
namespace Themia.MultiTenancy;

/// <summary>
/// Options for the tenant-presence guard.
/// </summary>
public sealed class TenantGuardOptions
{
    /// <summary>
    /// Roles permitted to operate without a resolved tenant (e.g. a cross-tenant SaaS admin).
    /// Empty by default (no bypass). Checked via <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole"/>.
    /// </summary>
    public IReadOnlyCollection<string> PrivilegedRoles { get; set; } = [];
}
```

- [ ] **Step 5: Write the verdict + Evaluate**

Create `src/framework/Themia.MultiTenancy/Guard/TenantGuard.cs`:

```csharp
using System.Security.Claims;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy;

/// <summary>
/// Outcome of a tenant-presence evaluation.
/// </summary>
public enum TenantGuardVerdict
{
    /// <summary>The request may proceed.</summary>
    Allow = 0,

    /// <summary>No authenticated principal — the caller must authenticate (maps to HTTP 401).</summary>
    Unauthenticated = 1,

    /// <summary>Authenticated but no usable tenant is resolved (maps to HTTP 403).</summary>
    NoTenant = 2,
}

/// <summary>
/// Transport-agnostic tenant-presence decision. Pure and host-free so it can be unit-tested and reused
/// by any adapter (Mediator behavior today; an ASP.NET filter could reuse it later).
/// </summary>
public static class TenantGuard
{
    /// <summary>
    /// Evaluates whether a request with the given principal and resolved tenant may proceed.
    /// Precedence: skip &gt; authentication &gt; privileged-role &gt; tenant-presence.
    /// </summary>
    /// <param name="principal">The current principal, or <c>null</c> when there is none.</param>
    /// <param name="currentTenant">The resolved tenant, or <c>null</c> when none was resolved.</param>
    /// <param name="skipRequested">Whether the request opted out via <see cref="ISkipTenantValidation"/>.</param>
    /// <param name="privilegedRoles">Roles allowed to proceed without a tenant.</param>
    /// <returns>The guard verdict.</returns>
    public static TenantGuardVerdict Evaluate(
        ClaimsPrincipal? principal,
        TenantInfo? currentTenant,
        bool skipRequested,
        IReadOnlyCollection<string> privilegedRoles)
    {
        if (skipRequested)
        {
            return TenantGuardVerdict.Allow;
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return TenantGuardVerdict.Unauthenticated;
        }

        if (privilegedRoles.Count > 0 && privilegedRoles.Any(principal.IsInRole))
        {
            return TenantGuardVerdict.Allow;
        }

        return currentTenant is null ? TenantGuardVerdict.NoTenant : TenantGuardVerdict.Allow;
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Themia.MultiTenancy.Tests/Themia.MultiTenancy.Tests.csproj --filter "FullyQualifiedName~TenantGuardTests"`
Expected: PASS (8 tests).

- [ ] **Step 7: Update PublicAPI and clean-build**

Append to `src/framework/Themia.MultiTenancy/PublicAPI.Unshipped.txt` (keep `#nullable enable` first; order does not matter to the analyzer):

```text
Themia.MultiTenancy.ISkipTenantValidation
Themia.MultiTenancy.TenantGuard
Themia.MultiTenancy.TenantGuardOptions
Themia.MultiTenancy.TenantGuardOptions.TenantGuardOptions() -> void
Themia.MultiTenancy.TenantGuardOptions.PrivilegedRoles.get -> System.Collections.Generic.IReadOnlyCollection<string!>!
Themia.MultiTenancy.TenantGuardOptions.PrivilegedRoles.set -> void
Themia.MultiTenancy.TenantGuardVerdict
Themia.MultiTenancy.TenantGuardVerdict.Allow = 0 -> Themia.MultiTenancy.TenantGuardVerdict
Themia.MultiTenancy.TenantGuardVerdict.Unauthenticated = 1 -> Themia.MultiTenancy.TenantGuardVerdict
Themia.MultiTenancy.TenantGuardVerdict.NoTenant = 2 -> Themia.MultiTenancy.TenantGuardVerdict
static Themia.MultiTenancy.TenantGuard.Evaluate(System.Security.Claims.ClaimsPrincipal? principal, Themia.MultiTenancy.Abstractions.TenantInfo? currentTenant, bool skipRequested, System.Collections.Generic.IReadOnlyCollection<string!>! privilegedRoles) -> Themia.MultiTenancy.TenantGuardVerdict
```

Run: `dotnet build src/framework/Themia.MultiTenancy/Themia.MultiTenancy.csproj --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors. (If RS0016/RS0037 fire, reconcile each line to the exact signature the diagnostic prints.)

- [ ] **Step 8: Commit**

```bash
git add src/framework/Themia.MultiTenancy/Guard src/framework/Themia.MultiTenancy/Options/TenantGuardOptions.cs src/framework/Themia.MultiTenancy/PublicAPI.Unshipped.txt tests/Themia.MultiTenancy.Tests/Guard/TenantGuardTests.cs
git commit -m "feat: add neutral TenantGuard verdict + ISkipTenantValidation + TenantGuardOptions"
```

---

## Task 2: Scaffold the bridge package and its test project

**Files:**
- Create: `src/framework/Themia.MultiTenancy.Mediator/Themia.MultiTenancy.Mediator.csproj`, `.../PublicAPI.Shipped.txt`, `.../PublicAPI.Unshipped.txt`
- Create: `tests/Themia.MultiTenancy.Mediator.Tests/Themia.MultiTenancy.Mediator.Tests.csproj`

- [ ] **Step 1: Create the library csproj**

Create `src/framework/Themia.MultiTenancy.Mediator/Themia.MultiTenancy.Mediator.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.MultiTenancy.Mediator</PackageId>
    <Description>Themia multi-tenancy Mediator bridge — a tenant-presence IPipelineBehavior that enforces an authenticated principal and a resolved tenant, with an opt-out marker and a privileged-role bypass.</Description>
    <PackageTags>themia;multi-tenancy;tenant;mediator;pipeline;guard</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <!-- ClaimsPrincipal/IHttpContextAccessor via the shared framework reference (repo pattern). -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Mediator/Themia.Mediator.csproj" />
    <ProjectReference Include="../Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
    <ProjectReference Include="../../neutral/Themia.AspNetCore/Themia.AspNetCore.csproj" />
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create the PublicAPI files**

Create `src/framework/Themia.MultiTenancy.Mediator/PublicAPI.Shipped.txt`:

```text
#nullable enable
```

Create `src/framework/Themia.MultiTenancy.Mediator/PublicAPI.Unshipped.txt`:

```text
#nullable enable
```

- [ ] **Step 3: Create the test csproj**

Create `tests/Themia.MultiTenancy.Mediator.Tests/Themia.MultiTenancy.Mediator.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/framework/Themia.MultiTenancy.Mediator/Themia.MultiTenancy.Mediator.csproj" />
    <ProjectReference Include="../../src/neutral/Themia.AspNetCore/Themia.AspNetCore.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Add both projects to the solution**

Run:
```bash
dotnet sln Themia.sln add src/framework/Themia.MultiTenancy.Mediator/Themia.MultiTenancy.Mediator.csproj
dotnet sln Themia.sln add tests/Themia.MultiTenancy.Mediator.Tests/Themia.MultiTenancy.Mediator.Tests.csproj
```
Expected: "Project ... added to the solution." for both.

- [ ] **Step 5: Build the empty library to verify wiring**

Run: `dotnet build src/framework/Themia.MultiTenancy.Mediator/Themia.MultiTenancy.Mediator.csproj`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/framework/Themia.MultiTenancy.Mediator tests/Themia.MultiTenancy.Mediator.Tests Themia.sln
git commit -m "build: scaffold Themia.MultiTenancy.Mediator package + test project"
```

---

## Task 3: `TenantGuardBehavior` (Mediator adapter)

**Files:**
- Test: `tests/Themia.MultiTenancy.Mediator.Tests/TenantGuardBehaviorTests.cs`
- Create: `src/framework/Themia.MultiTenancy.Mediator/TenantGuardBehavior.cs`
- Modify: `src/framework/Themia.MultiTenancy.Mediator/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.MultiTenancy.Mediator.Tests/TenantGuardBehaviorTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.AspNetCore.Exceptions;
using Themia.Mediator.Abstractions;
using Themia.MultiTenancy;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Mediator;
using Xunit;

namespace Themia.MultiTenancy.Mediator.Tests;

public class TenantGuardBehaviorTests
{
    private static readonly TenantInfo Tenant = new("acme", "acme");

    private static ClaimsPrincipal Authed(params string[] roles) =>
        new(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)),
            authenticationType: "test", ClaimTypes.Name, ClaimTypes.Role));

    private static TenantGuardBehavior<TReq, string> Build<TReq>(
        ClaimsPrincipal? principal, TenantInfo? tenant, string[]? privileged, out CapturingLogger<TenantGuardBehavior<TReq, string>> logger)
        where TReq : IRequest<string>
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = principal is null ? null : new DefaultHttpContext { User = principal },
        };
        var options = Options.Create(new TenantGuardOptions { PrivilegedRoles = privileged ?? [] });
        logger = new CapturingLogger<TenantGuardBehavior<TReq, string>>();
        return new TenantGuardBehavior<TReq, string>(accessor, new FakeTenantAccessor(tenant), options, logger);
    }

    [Fact]
    public async Task Allow_InvokesNext_WhenAuthedWithTenant()
    {
        var behavior = Build<TestRequest>(Authed("User"), Tenant, null, out _);

        var result = await behavior.HandleAsync(new TestRequest(), _ => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task Unauthenticated_Throws_WhenNoPrincipal()
    {
        var behavior = Build<TestRequest>(principal: null, tenant: Tenant, null, out _);

        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            behavior.HandleAsync(new TestRequest(), _ => Task.FromResult("unused"), CancellationToken.None));
    }

    [Fact]
    public async Task NoTenant_ThrowsForbidden_AndLogsWarning_WhenAuthedTenantless()
    {
        var behavior = Build<TestRequest>(Authed("User"), tenant: null, null, out var logger);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            behavior.HandleAsync(new TestRequest(), _ => Task.FromResult("unused"), CancellationToken.None));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Skip_InvokesNext_EvenWhenUnauthenticatedAndTenantless()
    {
        var behavior = Build<SkippableRequest>(principal: null, tenant: null, null, out _);

        var result = await behavior.HandleAsync(new SkippableRequest(), _ => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task PrivilegedRole_InvokesNext_WithNullTenant()
    {
        var behavior = Build<TestRequest>(Authed("SaaSAdmin"), tenant: null, ["SaaSAdmin"], out _);

        var result = await behavior.HandleAsync(new TestRequest(), _ => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }
}

file sealed record TestRequest : IRequest<string>;

file sealed record SkippableRequest : IRequest<string>, ISkipTenantValidation;

file sealed class FakeTenantAccessor(TenantInfo? current) : ITenantAccessor
{
    public TenantInfo? Current { get; } = current;
}

file sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.MultiTenancy.Mediator.Tests/Themia.MultiTenancy.Mediator.Tests.csproj --filter "FullyQualifiedName~TenantGuardBehaviorTests"`
Expected: FAIL — compile error, `TenantGuardBehavior` does not exist.

- [ ] **Step 3: Write the behavior**

Create `src/framework/Themia.MultiTenancy.Mediator/TenantGuardBehavior.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.AspNetCore.Exceptions;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Pipelines;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Mediator;

/// <summary>
/// Mediator pipeline behavior that enforces tenant presence: an unauthenticated principal yields
/// <see cref="UnauthorizedException"/> (HTTP 401); an authenticated principal with no resolved tenant
/// yields <see cref="ForbiddenException"/> (HTTP 403) and a warning. A request implementing
/// <see cref="ISkipTenantValidation"/> bypasses the guard, and a principal in a configured
/// <see cref="TenantGuardOptions.PrivilegedRoles"/> bypasses the tenant check. Register it to run early
/// in the pipeline (execution order follows registration order).
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
public sealed class TenantGuardBehavior<TRequest, TResponse>(
    IHttpContextAccessor httpContextAccessor,
    ITenantAccessor tenantAccessor,
    IOptions<TenantGuardOptions> options,
    ILogger<TenantGuardBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <inheritdoc />
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerContinuation<TResponse> next,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        var skip = request is ISkipTenantValidation;
        var principal = httpContextAccessor.HttpContext?.User;

        var verdict = TenantGuard.Evaluate(principal, tenantAccessor.Current, skip, options.Value.PrivilegedRoles);
        switch (verdict)
        {
            case TenantGuardVerdict.Unauthenticated:
                throw new UnauthorizedException("Authentication is required.");
            case TenantGuardVerdict.NoTenant:
                logger.LogWarning(
                    "Authenticated principal with no usable tenant for {RequestType} (UserId: {UserId}, Roles: {Roles})",
                    typeof(TRequest).Name, UserId(principal), Roles(principal));
                throw new ForbiddenException("A tenant context is required for this request.");
            default:
                return await next(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? UserId(ClaimsPrincipal? principal) =>
        principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    private static string Roles(ClaimsPrincipal? principal) =>
        principal is null ? string.Empty : string.Join(",", principal.FindAll(ClaimTypes.Role).Select(c => c.Value));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.MultiTenancy.Mediator.Tests/Themia.MultiTenancy.Mediator.Tests.csproj --filter "FullyQualifiedName~TenantGuardBehaviorTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Update PublicAPI**

Append to `src/framework/Themia.MultiTenancy.Mediator/PublicAPI.Unshipped.txt`:

```text
Themia.MultiTenancy.Mediator.TenantGuardBehavior<TRequest, TResponse>
Themia.MultiTenancy.Mediator.TenantGuardBehavior<TRequest, TResponse>.TenantGuardBehavior(Microsoft.AspNetCore.Http.IHttpContextAccessor! httpContextAccessor, Themia.MultiTenancy.Abstractions.ITenantAccessor! tenantAccessor, Microsoft.Extensions.Options.IOptions<Themia.MultiTenancy.TenantGuardOptions!>! options, Microsoft.Extensions.Logging.ILogger<Themia.MultiTenancy.Mediator.TenantGuardBehavior<TRequest, TResponse>!>! logger) -> void
Themia.MultiTenancy.Mediator.TenantGuardBehavior<TRequest, TResponse>.HandleAsync(TRequest request, Themia.Mediator.Pipelines.RequestHandlerContinuation<TResponse>! next, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<TResponse>!
```

- [ ] **Step 6: Commit**

```bash
git add src/framework/Themia.MultiTenancy.Mediator/TenantGuardBehavior.cs src/framework/Themia.MultiTenancy.Mediator/PublicAPI.Unshipped.txt tests/Themia.MultiTenancy.Mediator.Tests/TenantGuardBehaviorTests.cs
git commit -m "feat: add TenantGuardBehavior (tenant-presence IPipelineBehavior)"
```

---

## Task 4: `AddThemiaTenantGuard` registration extension

**Files:**
- Test: `tests/Themia.MultiTenancy.Mediator.Tests/TenantGuardRegistrationTests.cs`
- Create: `src/framework/Themia.MultiTenancy.Mediator/TenantGuardServiceCollectionExtensions.cs`
- Modify: `src/framework/Themia.MultiTenancy.Mediator/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.MultiTenancy.Mediator.Tests/TenantGuardRegistrationTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Mediator.Abstractions;
using Themia.MultiTenancy;
using Themia.MultiTenancy.Mediator;
using Xunit;

namespace Themia.MultiTenancy.Mediator.Tests;

public class TenantGuardRegistrationTests
{
    [Fact]
    public void AddThemiaTenantGuard_RegistersOpenGenericBehavior()
    {
        var services = new ServiceCollection();

        services.AddThemiaTenantGuard();

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IPipelineBehavior<,>) &&
            d.ImplementationType == typeof(TenantGuardBehavior<,>));
    }

    [Fact]
    public void AddThemiaTenantGuard_WithConfigure_SetsPrivilegedRoles()
    {
        var services = new ServiceCollection();

        services.AddThemiaTenantGuard(o => o.PrivilegedRoles = ["SaaSAdmin"]);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TenantGuardOptions>>().Value;
        Assert.Contains("SaaSAdmin", options.PrivilegedRoles);
    }

    [Fact]
    public void AddThemiaTenantGuard_WithoutConfigure_DefaultsToEmptyPrivilegedRoles()
    {
        var services = new ServiceCollection();

        services.AddThemiaTenantGuard();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<TenantGuardOptions>>().Value;
        Assert.Empty(options.PrivilegedRoles);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.MultiTenancy.Mediator.Tests/Themia.MultiTenancy.Mediator.Tests.csproj --filter "FullyQualifiedName~TenantGuardRegistrationTests"`
Expected: FAIL — compile error, `AddThemiaTenantGuard` does not exist.

- [ ] **Step 3: Write the extension**

Create `src/framework/Themia.MultiTenancy.Mediator/TenantGuardServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Mediator.Abstractions;

namespace Themia.MultiTenancy.Mediator;

/// <summary>
/// Registration helpers for the tenant-presence guard behavior.
/// </summary>
public static class TenantGuardServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TenantGuardBehavior{TRequest, TResponse}"/> as a Mediator pipeline behavior.
    /// Call this so the guard runs early in the pipeline (execution order follows registration order),
    /// before validation and the handler.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration for <see cref="TenantGuardOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaTenantGuard(
        this IServiceCollection services,
        Action<TenantGuardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TenantGuardOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantGuardBehavior<,>));
        return services;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.MultiTenancy.Mediator.Tests/Themia.MultiTenancy.Mediator.Tests.csproj --filter "FullyQualifiedName~TenantGuardRegistrationTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Update PublicAPI and clean-build the package**

Append to `src/framework/Themia.MultiTenancy.Mediator/PublicAPI.Unshipped.txt`:

```text
Themia.MultiTenancy.Mediator.TenantGuardServiceCollectionExtensions
static Themia.MultiTenancy.Mediator.TenantGuardServiceCollectionExtensions.AddThemiaTenantGuard(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, System.Action<Themia.MultiTenancy.TenantGuardOptions!>? configure = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

Run: `dotnet build src/framework/Themia.MultiTenancy.Mediator/Themia.MultiTenancy.Mediator.csproj --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors. (If RS0016/RS0037 fire, reconcile each line to the exact signature the diagnostic prints.)

- [ ] **Step 6: Commit**

```bash
git add src/framework/Themia.MultiTenancy.Mediator/TenantGuardServiceCollectionExtensions.cs src/framework/Themia.MultiTenancy.Mediator/PublicAPI.Unshipped.txt tests/Themia.MultiTenancy.Mediator.Tests/TenantGuardRegistrationTests.cs
git commit -m "feat: add AddThemiaTenantGuard registration extension"
```

---

## Task 5: Version bump to 0.5.7 and full verification

**Files:**
- Modify: `Directory.Build.props`

- [ ] **Step 1: Bump the shared version**

In `Directory.Build.props`, change the version line (inside the `Label="Version"` PropertyGroup):

```xml
    <Version>0.5.7</Version>
```

(from `<Version>0.5.6</Version>`)

- [ ] **Step 2: Full clean build (all TFMs)**

Run: `dotnet build Themia.sln --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 3: Full test run**

Run: `dotnet test Themia.sln`
Expected: All tests pass, including the new `TenantGuardTests`, `TenantGuardBehaviorTests`, and `TenantGuardRegistrationTests`.

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props
git commit -m "chore: bump version to 0.5.7 (tenant-presence guard)"
```

---

## Self-Review

**1. Spec coverage:**
- §2 neutral core (`TenantGuardVerdict`, `ISkipTenantValidation`, `TenantGuardOptions`, `TenantGuard.Evaluate`, precedence skip>auth>role>tenant) → Task 1. ✓
- §3 bridge package + `TenantGuardBehavior` + `AddThemiaTenantGuard` + early-ordering note → Tasks 2, 3, 4. ✓
- §4 error mapping (Unauthorized→401/Forbidden→403 via existing middleware) + WARN-only-on-NoTenant with UserId/Roles/RequestType, no PII → Task 3 behavior + test asserting the warning. ✓
- §5 edge cases (no HTTP context → Unauthenticated/fail-closed; not-authenticated → Unauthenticated; empty PrivilegedRoles → no bypass) → Task 1 tests (`Unauthenticated_WhenNoPrincipal`, `EmptyPrivilegedRoles_NoBypass`) + Task 3 test (`Skip_InvokesNext_EvenWhenUnauthenticatedAndTenantless`, `Unauthenticated_Throws_WhenNoPrincipal`). ✓
- §6 out-of-scope (no ASP.NET filter, no ISkipAuthValidation, fixed mapping) → no tasks, correctly omitted. ✓
- §7 testing (verdict truth table; behavior Allow/Unauthenticated/NoTenant+WARN/skip/privileged; registration) → Tasks 1, 3, 4. ✓
- Version 0.5.7 → Task 5. ✓

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to Task N"; every code, PublicAPI, and command step is literal. ✓

**3. Type consistency:** `TenantGuard.Evaluate(ClaimsPrincipal?, TenantInfo?, bool, IReadOnlyCollection<string>)` identical across Task 1 def, Task 1 tests, and Task 3 behavior; `TenantGuardVerdict.{Allow,Unauthenticated,NoTenant}` consistent; `TenantGuardOptions.PrivilegedRoles` consistent (Tasks 1/3/4); `TenantGuardBehavior<TRequest,TResponse>` ctor signature matches between Task 3 code, Task 3 tests, and the Task 3 PublicAPI line; `ISkipTenantValidation` namespace `Themia.MultiTenancy` consistent (Task 1 def, Task 3 `SkippableRequest`); `AddThemiaTenantGuard` signature matches Task 4 code, tests, and PublicAPI. `TenantInfo("acme","acme")` matches the positional `TenantInfo(string Id, string Identifier, …)` ctor. `RequestHandlerContinuation<TResponse>` is `Themia.Mediator.Pipelines`. ✓
