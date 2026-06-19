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
