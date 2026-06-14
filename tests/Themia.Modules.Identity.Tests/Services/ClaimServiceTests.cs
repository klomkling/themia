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
    private readonly ClaimService sut;
    private readonly Guid userId = Guid.NewGuid();
    private readonly Guid roleId = Guid.NewGuid();

    public ClaimServiceTests()
    {
        var tenant = new TenantId("acme");
        sut = new ClaimService(
            new FakeRepository<UserClaim>(userClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<RoleClaim>(roleClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<UserRole>(memberships, ur => ur.Id) { AmbientTenant = tenant },
            new FakeUnitOfWork());
    }

    [Fact]
    public async Task AddUserClaim_then_GetEffectiveClaims_includes_it()
    {
        await sut.AddUserClaimAsync(userId, "perm", "read");
        var claims = await sut.GetEffectiveClaimsAsync(userId);
        Assert.Contains(claims, c => c is { Type: "perm", Value: "read" });
    }

    [Fact]
    public async Task GetEffectiveClaims_unions_user_and_role_claims_distinctly()
    {
        // user directly has perm:read; the user's role has perm:read (dup) and perm:write.
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
        await sut.AddUserClaimAsync(userId, "perm", "read");
        await sut.AddUserClaimAsync(userId, "perm", "write");

        Assert.True(await sut.RemoveUserClaimAsync(userId, "perm", "read"));

        var claims = await sut.GetEffectiveClaimsAsync(userId);
        Assert.DoesNotContain(claims, c => c.Value == "read");
        Assert.Contains(claims, c => c.Value == "write");
    }
}
