using Microsoft.Extensions.Caching.Memory;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

/// <summary>ReplaceRoleClaimsAsync swaps a role's claims of the given types in one transaction and rotates each
/// holder's security stamp at most once; an unchanged set must not rotate anything.</summary>
public class ReplaceRoleClaimsTests
{
    private const string Legacy = "perm";
    private const string Leveled = "perm.level";
    private static readonly TenantId Tenant = new("acme");
    private static readonly string[] Types = [Legacy, Leveled];

    private readonly List<RoleClaim> roleClaims = [];
    private readonly List<UserRole> memberships = [];
    private readonly List<User> users = [];
    private readonly List<Role> roles = [];
    private readonly ClaimService claims;
    private int stampWrites;

    public ReplaceRoleClaimsTests()
    {
        var userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Tenant, BeforeUpdateWhere = _ => stampWrites++ };
        claims = new ClaimService(
            new FakeRepository<UserClaim>([], c => c.Id) { AmbientTenant = Tenant },
            new FakeRepository<RoleClaim>(roleClaims, c => c.Id) { AmbientTenant = Tenant },
            new FakeRepository<UserRole>(memberships, m => m.Id) { AmbientTenant = Tenant },
            userRepo,
            new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = Tenant },
            new FakeUnitOfWork(),
            new MemoryCache(new MemoryCacheOptions()));
    }

    private Role SeedRole(params User[] holders)
    {
        var role = new Role { Name = "r", NormalizedName = "R", TenantId = Tenant };
        role.SetId(Guid.NewGuid());
        roles.Add(role);
        foreach (var holder in holders)
        {
            memberships.Add(new UserRole { Id = Guid.NewGuid(), UserId = holder.Id, RoleId = role.Id });
        }

        return role;
    }

    private User SeedUser()
    {
        var user = new User { UserName = "u", NormalizedUserName = "U", TenantId = Tenant };
        user.SetId(Guid.NewGuid());
        users.Add(user);
        return user;
    }

    private void SeedClaim(Role role, string type, string value)
    {
        var claim = new RoleClaim { RoleId = role.Id, ClaimType = type, ClaimValue = value };
        claim.SetId(Guid.NewGuid());
        roleClaims.Add(claim);
    }

    private IEnumerable<(string Type, string Value)> Current(Role role) =>
        roleClaims.Where(c => c.RoleId == role.Id).Select(c => (c.ClaimType, c.ClaimValue)).OrderBy(c => c.ClaimType).ThenBy(c => c.ClaimValue);

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldSwapTheSet_WhenDesiredDiffers()
    {
        var role = SeedRole(SeedUser());
        SeedClaim(role, Legacy, "a");
        SeedClaim(role, Leveled, "1");

        await claims.ReplaceRoleClaimsAsync(role.Id, Types, [(Legacy, "b"), (Leveled, "1")]);

        Assert.Equal([(Legacy, "b"), (Leveled, "1")], Current(role));
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldRotateEachHolderOnce_WhenSeveralClaimsChange()
    {
        var holder = SeedUser();
        var role = SeedRole(holder);
        SeedClaim(role, Legacy, "a");
        SeedClaim(role, Leveled, "1");
        var before = holder.SecurityStamp;

        await claims.ReplaceRoleClaimsAsync(role.Id, Types, [(Legacy, "b"), (Leveled, "2")]);

        Assert.NotEqual(before, holder.SecurityStamp);
        Assert.Equal(1, stampWrites);
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldNotRotateAnyStamp_WhenDesiredEqualsCurrent()
    {
        var holder = SeedUser();
        var role = SeedRole(holder);
        SeedClaim(role, Legacy, "a");
        var before = holder.SecurityStamp;

        await claims.ReplaceRoleClaimsAsync(role.Id, Types, [(Legacy, "a")]);

        Assert.Equal(before, holder.SecurityStamp);
        Assert.Equal(0, stampWrites);
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldLeaveOtherTypesUntouched()
    {
        var role = SeedRole(SeedUser());
        SeedClaim(role, "other", "keep");
        SeedClaim(role, Legacy, "a");

        await claims.ReplaceRoleClaimsAsync(role.Id, [Legacy], []);

        Assert.Equal([("other", "keep")], Current(role));
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldCollapseDuplicateRows_WhenThePairIsKept()
    {
        var role = SeedRole(SeedUser());
        SeedClaim(role, Legacy, "a");
        SeedClaim(role, Legacy, "a");

        await claims.ReplaceRoleClaimsAsync(role.Id, [Legacy], [(Legacy, "a")]);

        Assert.Equal([(Legacy, "a")], Current(role));
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldInsertOnce_WhenDesiredListsAPairTwice()
    {
        var role = SeedRole(SeedUser());

        await claims.ReplaceRoleClaimsAsync(role.Id, [Legacy], [(Legacy, "a"), (Legacy, "a")]);

        Assert.Equal([(Legacy, "a")], Current(role));
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldThrow_WhenDesiredTypeIsOutsideClaimTypes()
    {
        var role = SeedRole(SeedUser());

        await Assert.ThrowsAsync<ArgumentException>(
            () => claims.ReplaceRoleClaimsAsync(role.Id, [Legacy], [(Leveled, "1")]));
        Assert.Empty(roleClaims);
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldThrow_WhenATypeIsReserved()
    {
        var role = SeedRole(SeedUser());

        await Assert.ThrowsAsync<ArgumentException>(
            () => claims.ReplaceRoleClaimsAsync(role.Id, ["sub"], [("sub", "x")]));
        Assert.Empty(roleClaims);
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldThrow_WhenTheRoleIsNotInScope()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => claims.ReplaceRoleClaimsAsync(Guid.NewGuid(), [Legacy], [(Legacy, "a")]));
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldThrow_WhenClaimTypesIsEmpty()
    {
        var role = SeedRole(SeedUser());

        await Assert.ThrowsAsync<ArgumentException>(() => claims.ReplaceRoleClaimsAsync(role.Id, [], []));
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldThrow_WhenADesiredValueIsNull()
    {
        var role = SeedRole(SeedUser());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => claims.ReplaceRoleClaimsAsync(role.Id, [Legacy], [(Legacy, null!)]));
        Assert.Empty(roleClaims);
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldRevokeEveryRowOfTheTypesAndRotateOnce_WhenDesiredIsEmpty()
    {
        var holder = SeedUser();
        var role = SeedRole(holder);
        SeedClaim(role, Legacy, "a");
        SeedClaim(role, Leveled, "1");
        SeedClaim(role, "other", "keep");
        var before = holder.SecurityStamp;

        await claims.ReplaceRoleClaimsAsync(role.Id, Types, []);

        Assert.Equal([("other", "keep")], Current(role));
        Assert.NotEqual(before, holder.SecurityStamp);
        Assert.Equal(1, stampWrites);
    }

    [Fact]
    public async Task ReplaceRoleClaimsAsync_ShouldTouchTheRoleRow_EvenWhenNothingChanges()
    {
        var role = SeedRole(SeedUser());
        SeedClaim(role, Legacy, "a");
        Assert.Null(role.LastModifiedAt);

        await claims.ReplaceRoleClaimsAsync(role.Id, [Legacy], [(Legacy, "a")]);

        Assert.NotNull(role.LastModifiedAt);
    }
}
