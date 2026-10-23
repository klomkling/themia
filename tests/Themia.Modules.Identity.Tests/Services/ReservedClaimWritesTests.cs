using System.Security.Claims;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Principal;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

/// <summary>Claims Themia issues itself cannot be written through the claim APIs, and a stored row of such a type
/// (written before this rule) never reaches a principal, so it cannot shadow the issued claim.</summary>
public class ReservedClaimWritesTests
{
    private static readonly TenantId Tenant = new("acme");
    private readonly List<UserClaim> userClaims = [];
    private readonly List<RoleClaim> roleClaims = [];
    private readonly List<UserRole> memberships = [];
    private readonly List<User> users = [];
    private readonly List<Role> roles = [];
    private readonly ClaimService claims;
    private readonly ClaimsPrincipalFactory factory;

    public ReservedClaimWritesTests()
    {
        var userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Tenant };
        var roleRepo = new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = Tenant };
        var membershipRepo = new FakeRepository<UserRole>(memberships, m => m.Id) { AmbientTenant = Tenant };
        claims = new ClaimService(
            new FakeRepository<UserClaim>(userClaims, c => c.Id) { AmbientTenant = Tenant },
            new FakeRepository<RoleClaim>(roleClaims, c => c.Id) { AmbientTenant = Tenant },
            membershipRepo, userRepo, roleRepo, new FakeUnitOfWork());
        factory = new ClaimsPrincipalFactory(membershipRepo, roleRepo, claims, new TenantContext(Tenant));
    }

    private User SeedUser()
    {
        var user = new User { UserName = "alice", NormalizedUserName = "ALICE", TenantId = Tenant };
        user.SetId(Guid.NewGuid());
        users.Add(user);
        return user;
    }

    private Role SeedRole()
    {
        var role = new Role { Name = "Admin", NormalizedName = "ADMIN", TenantId = Tenant };
        role.SetId(Guid.NewGuid());
        roles.Add(role);
        return role;
    }

    [Theory]
    [InlineData("sid")]
    [InlineData("Sid")]
    [InlineData("ROLE")]
    [InlineData("HTTP://SCHEMAS.MICROSOFT.COM/WS/2008/06/IDENTITY/CLAIMS/ROLE")]
    [InlineData(IdentityClaimTypes.SecurityStamp)]
    [InlineData(IdentityClaimTypes.IsPlatform)]
    [InlineData(IdentityClaimTypes.TenantId)]
    [InlineData(ClaimTypes.Role)]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public async Task AddUserClaim_refuses_a_reserved_type_and_stores_nothing(string claimType)
    {
        var user = SeedUser();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => claims.AddUserClaimAsync(user.Id, claimType, "x"));

        Assert.Contains("reserved", ex.Message);
        Assert.Empty(userClaims);
    }

    [Theory]
    [InlineData("sid")]
    [InlineData(IdentityClaimTypes.IsPlatform)]
    [InlineData(ClaimTypes.Role)]
    public async Task AddRoleClaim_refuses_a_reserved_type_and_stores_nothing(string claimType)
    {
        var role = SeedRole();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => claims.AddRoleClaimAsync(role.Id, claimType, "x"));

        Assert.Contains("reserved", ex.Message);
        Assert.Empty(roleClaims);
    }

    [Fact]
    public async Task AddUserClaim_still_accepts_an_app_claim_type()
    {
        var user = SeedUser();
        await claims.AddUserClaimAsync(user.Id, "perm", "read");
        Assert.Single(userClaims);
    }

    [Fact]
    public async Task A_stored_reserved_claim_can_still_be_removed()
    {
        var user = SeedUser();
        userClaims.Add(new UserClaim { Id = Guid.NewGuid(), UserId = user.Id, ClaimType = "sid", ClaimValue = "stale" });

        Assert.True(await claims.RemoveUserClaimAsync(user.Id, "sid", "stale"));

        Assert.Empty(userClaims);
    }

    [Fact]
    public async Task The_factory_drops_stored_reserved_claims_so_they_cannot_shadow_the_issued_ones()
    {
        var user = SeedUser();
        var role = SeedRole();
        memberships.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = role.Id });
        // Rows written before the rule existed: one direct, one through the role.
        userClaims.Add(new UserClaim { Id = Guid.NewGuid(), UserId = user.Id, ClaimType = IdentityClaimTypes.SecurityStamp, ClaimValue = "forged-stamp" });
        userClaims.Add(new UserClaim { Id = Guid.NewGuid(), UserId = user.Id, ClaimType = "sid", ClaimValue = "forged-sid" });
        userClaims.Add(new UserClaim { Id = Guid.NewGuid(), UserId = user.Id, ClaimType = "ROLE", ClaimValue = "SuperAdmin" });
        userClaims.Add(new UserClaim { Id = Guid.NewGuid(), UserId = user.Id, ClaimType = ClaimTypes.Role.ToUpperInvariant(), ClaimValue = "SuperAdmin" });
        userClaims.Add(new UserClaim { Id = Guid.NewGuid(), UserId = user.Id, ClaimType = "perm", ClaimValue = "read" });
        roleClaims.Add(new RoleClaim { Id = Guid.NewGuid(), RoleId = role.Id, ClaimType = IdentityClaimTypes.IsPlatform, ClaimValue = "true" });
        roleClaims.Add(new RoleClaim { Id = Guid.NewGuid(), RoleId = role.Id, ClaimType = ClaimTypes.Role, ClaimValue = "SuperAdmin" });
        roleClaims.Add(new RoleClaim { Id = Guid.NewGuid(), RoleId = role.Id, ClaimType = "perm", ClaimValue = "write" });

        var principal = await factory.CreateAsync(user, "test");

        Assert.Equal([user.SecurityStamp], principal.FindAll(IdentityClaimTypes.SecurityStamp).Select(c => c.Value));
        Assert.Null(principal.FindFirst("sid"));
        Assert.Null(principal.FindFirst(IdentityClaimTypes.IsPlatform));   // a tenant user has no platform marker
        Assert.False(principal.IsInRole("SuperAdmin"));
        Assert.True(principal.IsInRole("Admin"));                          // the real role claim survives
        Assert.Contains(principal.Claims, c => c is { Type: "perm", Value: "read" });
        Assert.Contains(principal.Claims, c => c is { Type: "perm", Value: "write" });
    }
}
