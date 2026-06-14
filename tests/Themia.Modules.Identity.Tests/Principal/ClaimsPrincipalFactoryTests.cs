using System.Security.Claims;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Principal;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Principal;

public class ClaimsPrincipalFactoryTests
{
    private readonly List<Role> roles = [];
    private readonly List<UserRole> memberships = [];
    private readonly List<UserClaim> userClaims = [];
    private readonly List<RoleClaim> roleClaims = [];
    private readonly ClaimsPrincipalFactory sut;

    public ClaimsPrincipalFactoryTests()
    {
        var tenant = new TenantId("acme");
        var claimService = new ClaimService(
            new FakeRepository<UserClaim>(userClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<RoleClaim>(roleClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<UserRole>(memberships, ur => ur.Id) { AmbientTenant = tenant },
            new FakeUnitOfWork());

        sut = new ClaimsPrincipalFactory(
            new FakeRepository<UserRole>(memberships, ur => ur.Id) { AmbientTenant = tenant },
            new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = tenant },
            claimService);
    }

    private User MakeUser(TenantId? tenant)
    {
        var user = new User { UserName = "alice", NormalizedUserName = "ALICE", TenantId = tenant };
        user.SetId(Guid.NewGuid());
        return user;
    }

    [Fact]
    public async Task Creates_principal_with_subject_name_role_and_effective_claims()
    {
        var user = MakeUser(new TenantId("acme"));
        var roleId = Guid.NewGuid();
        var role = new Role { Name = "Admin", NormalizedName = "ADMIN", TenantId = new TenantId("acme") };
        role.SetId(roleId);
        roles.Add(role);
        memberships.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = roleId });
        roleClaims.Add(new RoleClaim { Id = Guid.NewGuid(), RoleId = roleId, ClaimType = "perm", ClaimValue = "write" });

        var principal = await sut.CreateAsync(user, "Identity.Application");

        Assert.Equal(user.Id.ToString(), principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        Assert.Equal("alice", principal.FindFirst(ClaimTypes.Name)!.Value);
        Assert.True(principal.IsInRole("Admin"));
        Assert.Equal("acme", principal.FindFirst(IdentityClaimTypes.TenantId)!.Value);
        Assert.Contains(principal.Claims, c => c is { Type: "perm", Value: "write" });
        Assert.True(principal.Identity!.IsAuthenticated);
    }

    [Fact]
    public async Task Platform_user_has_no_tenant_claim()
    {
        var principal = await sut.CreateAsync(MakeUser(null), "Identity.Application");
        Assert.Null(principal.FindFirst(IdentityClaimTypes.TenantId));
    }
}
