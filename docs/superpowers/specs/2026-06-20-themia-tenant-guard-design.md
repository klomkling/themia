# Themia tenant-presence guard (0.5.7) Design

**Status:** Draft (2026-06-20)
**Scope:** Additive, **non-breaking** tenant *enforcement* for MultiTenancy: reject requests that reach a
handler with no usable tenant, with an opt-out marker and a privileged-role bypass. A transport-agnostic
verdict in `Themia.MultiTenancy` plus a Mediator `IPipelineBehavior` adapter in a new
`Themia.MultiTenancy.Mediator` bridge package. From coord #0004 (ezy-assets). Companion to
[`themia-architecture-overview.md`](../../themia-architecture-overview.md).

---

## 1. Milestone context

coord #0004: MultiTenancy *resolves* a tenant (`ITenantAccessor`) but does not *enforce* one. Every
multi-tenant consumer must reject requests that reach a handler with no usable tenant; ezy hand-rolled
this as a Mediator `IPipelineBehavior` (`TenantBehavior`, ezy PR #24). This slice provides a reusable
framework guard so consumers stop reinventing the 401-vs-403 + log semantics + bypass markers.

**Resolved decisions (do not relitigate):**
- **Do both** — a transport-agnostic enforcement *decision* in `Themia.MultiTenancy` (neutral) **and** a
  Mediator `IPipelineBehavior` *adapter*. They are orthogonal (what/where vs. mechanism), not competing.
- **Adapter lives in a new bridge package `Themia.MultiTenancy.Mediator`** (refs `Themia.Mediator` +
  `Themia.MultiTenancy` + `Themia.AspNetCore`). Keeps `Themia.MultiTenancy` free of a Mediator dependency
  so Dapper-only / non-Mediator apps don't drag in Mediator. Matches the repo's split-by-dependency
  philosophy.
- **No Identity-module dependency.** Auth state and the privileged-role bypass use `ClaimsPrincipal`
  (`IsAuthenticated` / `IsInRole`) via the `IHttpContextAccessor` MultiTenancy already registers —
  Identity's `ICurrentUser` is just a wrapper over the same `ClaimsPrincipal`.

Ships at **0.5.7** (additive feature → PATCH under the pre-1.0 milestone policy; MINOR/0.6.0 stays
reserved for Phase 2). Single shared monorepo version.

## 2. Neutral core (`Themia.MultiTenancy`)

Pure, host-agnostic, fully unit-testable — no Mediator, no HTTP, no Identity.

```csharp
namespace Themia.MultiTenancy;

public enum TenantGuardVerdict { Allow, Unauthenticated, NoTenant }

/// Marker on a request type → the guard steps aside entirely (full bypass).
public interface ISkipTenantValidation { }

public sealed class TenantGuardOptions
{
    /// Roles permitted to operate without a resolved tenant (e.g. cross-tenant SaaS admin).
    /// Empty by default (no bypass). Checked via ClaimsPrincipal.IsInRole.
    public IReadOnlyCollection<string> PrivilegedRoles { get; set; } = [];
}

public static class TenantGuard
{
    public static TenantGuardVerdict Evaluate(
        ClaimsPrincipal? principal,
        TenantInfo? currentTenant,
        bool skipRequested,
        IReadOnlyCollection<string> privilegedRoles)
    {
        if (skipRequested) return TenantGuardVerdict.Allow;                       // explicit per-request opt-out
        if (principal?.Identity?.IsAuthenticated != true) return TenantGuardVerdict.Unauthenticated; // -> 401
        if (privilegedRoles.Count > 0 && privilegedRoles.Any(principal.IsInRole)) return TenantGuardVerdict.Allow; // SaaS-admin
        if (currentTenant is null) return TenantGuardVerdict.NoTenant;            // -> 403
        return TenantGuardVerdict.Allow;
    }
}
```

**Precedence (deliberate):** `skip > auth > privileged-role > tenant-presence`.
- **skip = full bypass** — a request marked `ISkipTenantValidation` is exempt from *both* the auth and
  tenant checks, so login / refresh / public / system commands aren't forced to 401/403. Auth for those
  endpoints is still independently enforced upstream by ASP.NET `[Authorize]`/auth middleware where
  required.
- **Privileged role bypasses only the tenant check** — the principal must still be authenticated.
- **"No usable tenant" = `currentTenant is null`** (`ITenantAccessor.Current`; `TenantInfo.Identifier`
  is a required non-null value, so there is no "empty identifier" sub-case to handle).

`ISkipTenantValidation`, `TenantGuardOptions`, `TenantGuardVerdict`, and `TenantGuard` live in
`Themia.MultiTenancy` so domain request types can implement the marker without referencing the Mediator
bridge.

## 3. Mediator adapter (`Themia.MultiTenancy.Mediator`, new package)

New `net10.0` package; `ProjectReference` to `Themia.Mediator`, `Themia.MultiTenancy`, and
`Themia.AspNetCore` (for the typed exceptions). Tracks PublicAPI (Shipped/Unshipped) like the other
cross-cutting packages.

```csharp
public sealed class TenantGuardBehavior<TRequest, TResponse>(
    IHttpContextAccessor httpContextAccessor,
    ITenantAccessor tenantAccessor,
    IOptions<TenantGuardOptions> options,
    ILogger<TenantGuardBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request,
        RequestHandlerContinuation<TResponse> next, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        var skip = request is ISkipTenantValidation;
        var principal = httpContextAccessor.HttpContext?.User;
        var tenant = tenantAccessor.Current;

        var verdict = TenantGuard.Evaluate(principal, tenant, skip, options.Value.PrivilegedRoles);
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

    private static string? UserId(ClaimsPrincipal? p) => p?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    private static string Roles(ClaimsPrincipal? p) =>
        p is null ? "" : string.Join(",", p.FindAll(ClaimTypes.Role).Select(c => c.Value));
}
```

**Registration** (extension in the bridge package):
```csharp
services.AddThemiaTenantGuard(o => o.PrivilegedRoles = ["SaaSAdmin"]);
// → services.AddOptions<TenantGuardOptions>().Configure(...) (when a delegate is supplied)
// → services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantGuardBehavior<,>));
```
Mirrors how `Themia.Mediator` registers its built-in behaviors (open-generic
`AddScoped(typeof(IPipelineBehavior<,>), typeof(XBehavior<,>))`). **Execution order = registration
order**, so the guard should be registered to run **early** (before validation/handler); documented on
the extension. `IHttpContextAccessor` is already registered by `AddThemiaMultiTenancy`; the extension
does not re-register it.

## 4. Error mapping & logging

- `UnauthorizedException` → **401**, `ForbiddenException` → **403** via the existing
  `ProblemDetailsMiddleware` type→status map (`Themia.AspNetCore`). No new mapping is added.
- **WARN is logged only on `NoTenant`** (the surprising "authenticated but tenant-less" case), with
  `RequestType` (`typeof(TRequest).Name`), the `NameIdentifier` claim as `UserId`, and the role claims.
  **No name/email/PII** is logged. `Unauthenticated` and `Allow` are not logged by the guard (auth
  middleware owns 401 diagnostics; allowed requests are routine).
- Logs via `ILogger<T>` only; one log per handled verdict (no double-logging).

## 5. Edge cases

- **No HTTP context / no principal** (e.g. a background Mediator command): `principal` is null →
  `Unauthenticated`. **Fail-closed.** System/background commands that legitimately have no tenant opt out
  via `ISkipTenantValidation`.
- **Principal present but `Identity` null or not authenticated** → `Unauthenticated`.
- **`PrivilegedRoles` empty** (default) → no role bypass; behavior depends purely on auth + tenant.

## 6. Out of scope (deferred / YAGNI)

- **ASP.NET endpoint-filter / middleware adapter** — only the Mediator adapter is built now (ezy's actual
  mechanism). The neutral `TenantGuard.Evaluate` is transport-agnostic, so a filter adapter can be added
  later without touching the core.
- **A dedicated `ISkipAuthValidation` marker** — auth exemption is `[AllowAnonymous]`/auth-middleware's
  job; `ISkipTenantValidation` already fully bypasses the guard for tenant-less commands.
- **Configurable verdict→status mapping** — fixed: Unauthorized→401, Forbidden→403 via the existing seam.

## 7. Testing

- **`TenantGuard.Evaluate`** (neutral, no host): truth table over `(skip, authenticated, privileged-role,
  tenant)` covering every verdict and the precedence order (skip wins over unauthenticated; privileged
  role bypasses tenant but not auth; null tenant → NoTenant; happy path → Allow).
- **`TenantGuardBehavior`** (fakes for `IHttpContextAccessor`, `ITenantAccessor`, `IOptions`): Allow →
  `next` invoked and its response returned; `Unauthenticated` → `UnauthorizedException`; `NoTenant` →
  `ForbiddenException` **and** a WARN logged; `ISkipTenantValidation` request → `next` invoked (no throw)
  even when unauthenticated/tenant-less; privileged-role principal with null tenant → `next` invoked.
- **Registration**: `AddThemiaTenantGuard()` registers the open-generic `IPipelineBehavior<,>` →
  `TenantGuardBehavior<,>`; the optional configure delegate sets `PrivilegedRoles`.
- New package builds on `net10.0` with PublicAPI tracked; clean build (TWAE).
