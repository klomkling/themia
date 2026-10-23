using Microsoft.Extensions.Caching.Memory;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

/// <summary>A claim change must cut live access tokens, because apps read permissions from the token's claims; the
/// in-process validator cache must not keep serving the old stamp after a rotation.</summary>
public class SecurityStampRotationTests
{
    private static readonly TenantId Tenant = new("acme");
    private readonly List<UserClaim> userClaims = [];
    private readonly List<RoleClaim> roleClaims = [];
    private readonly List<UserRole> memberships = [];
    private readonly List<User> users = [];
    private readonly List<Role> roles = [];
    private readonly MemoryCache cache = new(new MemoryCacheOptions());
    private readonly ClaimService claims;
    private readonly RoleService roleService;
    private readonly UserService userService;
    private readonly SecurityStampValidator validator;

    public SecurityStampRotationTests()
    {
        var userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Tenant };
        var roleRepo = new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = Tenant };
        var membershipRepo = new FakeRepository<UserRole>(memberships, m => m.Id) { AmbientTenant = Tenant };
        var uow = new FakeUnitOfWork();
        claims = new ClaimService(
            new FakeRepository<UserClaim>(userClaims, c => c.Id) { AmbientTenant = Tenant },
            new FakeRepository<RoleClaim>(roleClaims, c => c.Id) { AmbientTenant = Tenant },
            membershipRepo, userRepo, roleRepo, uow, cache);
        roleService = new RoleService(userRepo, roleRepo, membershipRepo, uow, new DataFilterScope(), cache);
        userService = new UserService(
            userRepo, uow, new Argon2idPasswordHasher(), TimeProvider.System, new IdentityModuleOptions(), new DataFilterScope(),
            new FormattingOnlyPhoneNumberNormalizer(), new RecordingUserLifecycleHooks(), [], null, cache);
        validator = new SecurityStampValidator(userRepo, cache, new IdentityModuleOptions { SecurityStampCacheDuration = TimeSpan.FromMinutes(5) });
    }

    private User SeedUser()
    {
        var user = new User { UserName = "u", NormalizedUserName = "U", TenantId = Tenant };
        user.SetId(Guid.NewGuid());
        users.Add(user);
        return user;
    }

    private Role SeedRole(User? holder = null)
    {
        var role = new Role { Name = "r", NormalizedName = "R", TenantId = Tenant };
        role.SetId(Guid.NewGuid());
        roles.Add(role);
        if (holder is not null)
        {
            memberships.Add(new UserRole { Id = Guid.NewGuid(), UserId = holder.Id, RoleId = role.Id });
        }

        return role;
    }

    [Fact]
    public async Task Adding_a_role_claim_rotates_the_stamp_of_every_holder_and_only_them()
    {
        var holderA = SeedUser();
        var holderB = SeedUser();
        var bystander = SeedUser();
        var role = SeedRole(holderA);
        memberships.Add(new UserRole { Id = Guid.NewGuid(), UserId = holderB.Id, RoleId = role.Id });
        var (a, b, c) = (holderA.SecurityStamp, holderB.SecurityStamp, bystander.SecurityStamp);

        await claims.AddRoleClaimAsync(role.Id, "perm", "write");

        Assert.NotEqual(a, holderA.SecurityStamp);
        Assert.NotEqual(b, holderB.SecurityStamp);
        Assert.Equal(c, bystander.SecurityStamp);
    }

    [Fact]
    public async Task Removing_a_role_claim_rotates_the_stamp_of_every_holder()
    {
        var holder = SeedUser();
        var role = SeedRole(holder);
        await claims.AddRoleClaimAsync(role.Id, "perm", "write");
        var before = holder.SecurityStamp;

        Assert.True(await claims.RemoveRoleClaimAsync(role.Id, "perm", "write"));

        Assert.NotEqual(before, holder.SecurityStamp);
    }

    [Fact]
    public async Task Removing_a_role_claim_that_does_not_exist_keeps_the_stamps()
    {
        var holder = SeedUser();
        var role = SeedRole(holder);
        var before = holder.SecurityStamp;

        Assert.False(await claims.RemoveRoleClaimAsync(role.Id, "perm", "missing"));

        Assert.Equal(before, holder.SecurityStamp);
    }

    [Fact]
    public async Task Adding_and_removing_a_user_claim_rotates_that_users_stamp()
    {
        var user = SeedUser();
        var other = SeedUser();
        var otherBefore = other.SecurityStamp;
        var before = user.SecurityStamp;

        await claims.AddUserClaimAsync(user.Id, "perm", "read");
        var afterAdd = user.SecurityStamp;
        await claims.RemoveUserClaimAsync(user.Id, "perm", "read");

        Assert.NotEqual(before, afterAdd);
        Assert.NotEqual(afterAdd, user.SecurityStamp);
        Assert.Equal(otherBefore, other.SecurityStamp);
    }

    [Fact]
    public async Task A_role_claim_change_is_refused_by_the_validator_at_once_despite_the_cache()
    {
        var holder = SeedUser();
        var role = SeedRole(holder);
        var issued = holder.SecurityStamp;
        Assert.True(await validator.IsCurrentAsync(holder.Id, issued));   // now cached

        await claims.AddRoleClaimAsync(role.Id, "perm", "write");

        Assert.False(await validator.IsCurrentAsync(holder.Id, issued));
        Assert.True(await validator.IsCurrentAsync(holder.Id, holder.SecurityStamp));
    }

    [Fact]
    public async Task SetActive_and_SetPassword_evict_the_cached_stamp()
    {
        var user = SeedUser();
        var issued = user.SecurityStamp;
        Assert.True(await validator.IsCurrentAsync(user.Id, issued));

        await userService.SetPasswordAsync(user.Id, "new-pw");
        Assert.False(await validator.IsCurrentAsync(user.Id, issued));

        var afterPassword = user.SecurityStamp;
        Assert.True(await validator.IsCurrentAsync(user.Id, afterPassword));
        await userService.SetActiveAsync(user.Id, false);
        Assert.False(await validator.IsCurrentAsync(user.Id, afterPassword));
    }

    [Fact]
    public async Task Role_assignment_evicts_the_cached_stamp()
    {
        var user = SeedUser();
        var role = SeedRole();
        var issued = user.SecurityStamp;
        Assert.True(await validator.IsCurrentAsync(user.Id, issued));

        await roleService.AssignRoleAsync(user.Id, role.Id);

        Assert.False(await validator.IsCurrentAsync(user.Id, issued));
    }
}
