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
