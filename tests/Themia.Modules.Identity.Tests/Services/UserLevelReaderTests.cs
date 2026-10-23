using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class UserLevelReaderTests
{
    private static readonly TenantId Tenant = new("acme");
    private readonly List<User> users = [];
    private readonly List<Role> roles = [];
    private readonly List<UserRole> memberships = [];
    private readonly UserLevelReader sut;

    public UserLevelReaderTests()
    {
        sut = new UserLevelReader(
            new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Tenant },
            new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = Tenant },
            new FakeRepository<UserRole>(memberships, m => m.Id) { AmbientTenant = Tenant },
            new TenantContext(Tenant));
    }

    private Guid SeedUser(TenantId? tenant = null)
    {
        var user = new User { UserName = "u", NormalizedUserName = "U", TenantId = tenant ?? Tenant };
        user.SetId(Guid.NewGuid());
        users.Add(user);
        return user.Id;
    }

    private Guid SeedRole(RoleLevel level, TenantId? tenant = null, bool platform = false)
    {
        var role = new Role { Name = "r", NormalizedName = "R", Level = level, TenantId = platform ? null : tenant ?? Tenant };
        role.SetId(Guid.NewGuid());
        roles.Add(role);
        return role.Id;
    }

    private void Grant(Guid userId, Guid roleId) =>
        memberships.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId });

    [Fact]
    public async Task GetLevelAsync_returns_the_highest_level_among_the_users_roles()
    {
        var user = SeedUser();
        Grant(user, SeedRole(RoleLevel.Level4));
        Grant(user, SeedRole(RoleLevel.Level2));
        Grant(user, SeedRole(RoleLevel.Level3));

        Assert.Equal(RoleLevel.Level2, await sut.GetLevelAsync(user));
    }

    [Fact]
    public async Task GetLevelAsync_returns_Level5_when_the_user_has_no_roles()
    {
        Assert.Equal(RoleLevel.Level5, await sut.GetLevelAsync(SeedUser()));
    }

    [Fact]
    public async Task GetLevelAsync_returns_Level5_for_an_unknown_user()
    {
        Assert.Equal(RoleLevel.Level5, await sut.GetLevelAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetLevelAsync_counts_a_platform_role()
    {
        var user = SeedUser();
        Grant(user, SeedRole(RoleLevel.Level1, platform: true));

        Assert.Equal(RoleLevel.Level1, await sut.GetLevelAsync(user));
    }

    [Fact]
    public async Task GetLevelAsync_ignores_a_role_from_another_tenant()
    {
        var user = SeedUser();
        Grant(user, SeedRole(RoleLevel.Level1, new TenantId("other")));

        Assert.Equal(RoleLevel.Level5, await sut.GetLevelAsync(user));
    }

    [Fact]
    public async Task GetLevelAsync_ignores_a_soft_deleted_role()
    {
        var user = SeedUser();
        var roleId = SeedRole(RoleLevel.Level1);
        roles.Single(r => r.Id == roleId).IsDeleted = true;
        Grant(user, roleId);

        Assert.Equal(RoleLevel.Level5, await sut.GetLevelAsync(user));
    }

    [Fact]
    public async Task GetLevelAsync_returns_Level5_for_a_user_in_another_tenant()
    {
        var user = SeedUser(new TenantId("other"));
        Grant(user, SeedRole(RoleLevel.Level1));

        Assert.Equal(RoleLevel.Level5, await sut.GetLevelAsync(user));
    }
}
