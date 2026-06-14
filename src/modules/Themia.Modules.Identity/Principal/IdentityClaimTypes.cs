namespace Themia.Modules.Identity.Principal;

/// <summary>Themia-specific claim type URIs added to the principal alongside the standard <see cref="System.Security.Claims.ClaimTypes"/>.</summary>
public static class IdentityClaimTypes
{
    /// <summary>The user's tenant id. Absent for platform users.</summary>
    public const string TenantId = "themia:tenant_id";

    /// <summary>Positive marker (<c>"true"</c>) present only on platform (super-admin) principals.
    /// Platform status is read from this claim, not inferred from the absence of a tenant claim, so a
    /// principal lacking the marker is never treated as platform (fail-closed).</summary>
    public const string IsPlatform = "themia:is_platform";

    /// <summary>The user's security stamp, used to invalidate stale principals when credentials change.</summary>
    public const string SecurityStamp = "themia:security_stamp";
}
