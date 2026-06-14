using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class RoleServiceTests
{
    private readonly List<User> users = [];
    private readonly List<Role> roles = [];
    private readonly List<UserRole> memberships = [];
    private readonly FakeRepository<User> userRepo;
    private readonly FakeRepository<Role> roleRepo;
    private readonly FakeRepository<UserRole> membershipRepo;
    private readonly FakeUnitOfWork uow = new();
    private readonly RoleService sut;

    public RoleServiceTests()
    {
        var tenant = new TenantId("acme");
        userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = tenant };
        roleRepo = new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = tenant };
        membershipRepo = new FakeRepository<UserRole>(memberships, ur => ur.Id) { AmbientTenant = tenant };
        sut = new RoleService(userRepo, roleRepo, membershipRepo, uow);
    }

    private Guid SeedUser()
    {
        var u = new User { UserName = "u", NormalizedUserName = "U", TenantId = new TenantId("acme") };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u.Id;
    }

    [Fact]
    public async Task CreateAsync_creates_role_and_rejects_duplicate()
    {
        var id = await sut.CreateAsync("Admin", "Administrators");
        Assert.NotNull(id);
        Assert.Equal("ADMIN", Assert.Single(roles).NormalizedName);

        Assert.Null(await sut.CreateAsync("admin"));   // duplicate in scope
    }

    [Fact]
    public async Task AssignRoleAsync_then_GetRoleIds_reflects_membership()
    {
        var userId = SeedUser();
        var roleId = (await sut.CreateAsync("Editor"))!.Value;

        Assert.True(await sut.AssignRoleAsync(userId, roleId));
        Assert.Contains(roleId, await sut.GetRoleIdsAsync(userId));
    }

    [Fact]
    public async Task AssignRoleAsync_is_idempotent()
    {
        var userId = SeedUser();
        var roleId = (await sut.CreateAsync("Editor"))!.Value;

        Assert.True(await sut.AssignRoleAsync(userId, roleId));
        Assert.True(await sut.AssignRoleAsync(userId, roleId));
        Assert.Single(await sut.GetRoleIdsAsync(userId));
    }

    [Fact]
    public async Task AssignRoleAsync_fails_when_user_not_in_scope()
    {
        var roleId = (await sut.CreateAsync("Editor"))!.Value;
        Assert.False(await sut.AssignRoleAsync(Guid.NewGuid(), roleId));
    }

    [Fact]
    public async Task RemoveRoleAsync_removes_membership()
    {
        var userId = SeedUser();
        var roleId = (await sut.CreateAsync("Editor"))!.Value;
        await sut.AssignRoleAsync(userId, roleId);

        Assert.True(await sut.RemoveRoleAsync(userId, roleId));
        Assert.Empty(await sut.GetRoleIdsAsync(userId));
    }
}
