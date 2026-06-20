using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy;

/// <summary>
/// Options for the tenant-presence guard.
/// </summary>
public sealed class TenantGuardOptions
{
    private IReadOnlyCollection<string> _privilegedRoles = [];

    /// <summary>
    /// Optional check for whether a resolved tenant is usable. Return <c>true</c> to admit the tenant;
    /// return <c>false</c> to reject it — the guard then treats the request as having no tenant (403),
    /// exactly as if none had resolved. Lets a consumer reject semantically-invalid tenants that the
    /// format-level tenant-id validation done at resolution time can't catch — e.g. an int-typed consumer
    /// rejecting a non-positive id: <c>t =&gt; int.TryParse(t.Identifier, out var id) &amp;&amp; id &gt; 0</c>.
    /// A predicate that throws is treated as "reject" (deny, fail-closed) and logged. <c>null</c> (default)
    /// admits any resolved tenant. For liveness/DB-backed checks ("is the tenant active?"), prefer the
    /// resolver, not this synchronous predicate.
    /// </summary>
    public Func<TenantInfo, bool>? TenantValidator { get; set; }

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
