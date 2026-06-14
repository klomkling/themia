using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class ClaimServiceTests
{
    private readonly List<UserClaim> userClaims = [];
    private readonly List<RoleClaim> roleClaims = [];
    private readonly List<UserRole> memberships = [];
    private readonly List<User> users = [];
    private readonly List<Role> roles = [];
    private readonly TenantId tenant = new("acme");
    private readonly ClaimService sut;
    private readonly Guid userId = Guid.NewGuid();
    private readonly Guid roleId = Guid.NewGuid();

    public ClaimServiceTests()
    {
        sut = new ClaimService(
            new FakeRepository<UserClaim>(userClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<RoleClaim>(roleClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<UserRole>(memberships, ur => ur.Id) { AmbientTenant = tenant },
            new FakeRepository<User>(users, u => u.Id) { AmbientTenant = tenant },
            new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = tenant },
            new FakeUnitOfWork());
    }

    private void SeedUser(Guid id, TenantId? userTenant)
    {
        var u = new User { UserName = "u", NormalizedUserName = "U", TenantId = userTenant };
        u.SetId(id);
        users.Add(u);
    }

    private void SeedRole(Guid id, TenantId? roleTenant)
    {
        var r = new Role { Name = "r", NormalizedName = "R", TenantId = roleTenant };
        r.SetId(id);
        roles.Add(r);
    }

    [Fact]
    public async Task AddUserClaim_then_GetEffectiveClaims_includes_it()
    {
        SeedUser(userId, tenant);
        await sut.AddUserClaimAsync(userId, "perm", "read");
        var claims = await sut.GetEffectiveClaimsAsync(userId);
        Assert.Contains(claims, c => c is { Type: "perm", Value: "read" });
    }

    [Fact]
    public async Task GetEffectiveClaims_unions_user_and_role_claims_distinctly()
    {
        // user directly has perm:read; the user's role has perm:read (dup) and perm:write.
        SeedUser(userId, tenant);
        SeedRole(roleId, tenant);
        await sut.AddUserClaimAsync(userId, "perm", "read");
        memberships.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId });
        await sut.AddRoleClaimAsync(roleId, "perm", "read");
        await sut.AddRoleClaimAsync(roleId, "perm", "write");

        var claims = await sut.GetEffectiveClaimsAsync(userId);

        Assert.Equal(2, claims.Count);   // read (deduped) + write
        Assert.Contains(claims, c => c.Value == "read");
        Assert.Contains(claims, c => c.Value == "write");
    }

    [Fact]
    public async Task RemoveUserClaim_removes_only_the_matching_claim()
    {
        SeedUser(userId, tenant);
        await sut.AddUserClaimAsync(userId, "perm", "read");
        await sut.AddUserClaimAsync(userId, "perm", "write");

        Assert.True(await sut.RemoveUserClaimAsync(userId, "perm", "read"));

        var claims = await sut.GetEffectiveClaimsAsync(userId);
        Assert.DoesNotContain(claims, c => c.Value == "read");
        Assert.Contains(claims, c => c.Value == "write");
    }

    [Fact]
    public async Task AddUserClaimAsync_throws_when_user_is_in_another_tenant()
    {
        SeedUser(userId, new TenantId("other"));   // ambient is "acme"

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.AddUserClaimAsync(userId, "perm", "read"));

        Assert.Empty(userClaims);
    }

    [Fact]
    public async Task RemoveUserClaimAsync_returns_false_when_user_is_in_another_tenant()
    {
        SeedUser(userId, new TenantId("other"));   // ambient is "acme"
        // A pre-existing claim row (no tenant_id column on the child table).
        userClaims.Add(new UserClaim { Id = Guid.NewGuid(), UserId = userId, ClaimType = "perm", ClaimValue = "read" });

        Assert.False(await sut.RemoveUserClaimAsync(userId, "perm", "read"));
        Assert.Single(userClaims);   // untouched
    }

    [Fact]
    public async Task AddRoleClaimAsync_throws_when_role_is_in_another_tenant()
    {
        SeedRole(roleId, new TenantId("other"));   // ambient is "acme"

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.AddRoleClaimAsync(roleId, "perm", "read"));

        Assert.Empty(roleClaims);
    }

    [Fact]
    public async Task GetEffectiveClaimsAsync_returns_empty_for_user_in_another_tenant()
    {
        SeedUser(userId, new TenantId("other"));   // ambient is "acme"
        // A pre-existing claim row for that user (no tenant_id column on the child table).
        userClaims.Add(new UserClaim { Id = Guid.NewGuid(), UserId = userId, ClaimType = "perm", ClaimValue = "read" });

        var claims = await sut.GetEffectiveClaimsAsync(userId);

        Assert.Empty(claims);
    }

    [Fact]
    public async Task RemoveRoleClaimAsync_removes_matching_role_claim()
    {
        SeedRole(roleId, tenant);
        await sut.AddRoleClaimAsync(roleId, "perm", "read");
        await sut.AddRoleClaimAsync(roleId, "perm", "write");

        Assert.True(await sut.RemoveRoleClaimAsync(roleId, "perm", "read"));

        Assert.Single(roleClaims, c => c.ClaimValue == "write");
        Assert.DoesNotContain(roleClaims, c => c.ClaimValue == "read");
    }

    [Fact]
    public async Task RemoveRoleClaimAsync_returns_false_for_role_in_another_tenant()
    {
        SeedRole(roleId, new TenantId("other"));   // ambient is "acme"
        // A pre-existing role-claim row (no tenant_id column on the child table).
        roleClaims.Add(new RoleClaim { Id = Guid.NewGuid(), RoleId = roleId, ClaimType = "perm", ClaimValue = "read" });

        Assert.False(await sut.RemoveRoleClaimAsync(roleId, "perm", "read"));
        Assert.Single(roleClaims);   // untouched
    }
}
