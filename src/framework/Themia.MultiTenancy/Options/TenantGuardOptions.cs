namespace Themia.MultiTenancy;

/// <summary>
/// Options for the tenant-presence guard.
/// </summary>
public sealed class TenantGuardOptions
{
    private IReadOnlyCollection<string> _privilegedRoles = [];

    /// <summary>
    /// Roles permitted to operate without a resolved tenant (e.g. a cross-tenant SaaS admin).
    /// Empty by default (no bypass). Checked via <see cref="System.Security.Claims.ClaimsPrincipal.IsInRole"/>.
    /// Assigning <c>null</c> resets to empty so the no-bypass-by-default invariant always holds.
    /// </summary>
    public IReadOnlyCollection<string> PrivilegedRoles
    {
        get => _privilegedRoles;
        set => _privilegedRoles = value ?? [];
    }
}
