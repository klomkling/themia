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

    [Fact]
    public void Skip_Bypasses_WhenAuthedTenantlessAndNonPrivileged() =>
        // skip must win even on a path that would otherwise be NoTenant (proves skip precedes the
        // privileged-role/tenant checks, not just the unauthenticated one).
        Assert.Equal(TenantGuardVerdict.Allow,
            TenantGuard.Evaluate(Authed("User"), currentTenant: null, skipRequested: true, Privileged));

    [Fact]
    public void Evaluate_Throws_WhenPrivilegedRolesNull() =>
        Assert.Throws<ArgumentNullException>(() =>
            TenantGuard.Evaluate(Authed("User"), Tenant, false, null!));

    [Fact]
    public void TenantGuardOptions_NullPrivilegedRoles_ResetsToEmpty() =>
        Assert.Empty(new TenantGuardOptions { PrivilegedRoles = null! }.PrivilegedRoles);
}
