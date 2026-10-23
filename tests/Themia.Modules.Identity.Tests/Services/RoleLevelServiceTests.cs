using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class RoleLevelServiceTests
{
    private readonly List<Role> roles = [];
    private readonly RoleLevelService sut;
    private readonly RoleLevelService platformSut;

    public RoleLevelServiceTests()
    {
        var tenant = new TenantId("acme");
        var repo = new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = tenant };
        sut = new RoleLevelService(repo, new FakeUnitOfWork(), new DataFilterScope(), new TenantContext(tenant));
        platformSut = new RoleLevelService(
            new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = null },
            new FakeUnitOfWork(), new DataFilterScope(), new TenantContext(null));
    }

    private Role SeedPlatformRole()
    {
        var role = new Role { Name = "Root", NormalizedName = "ROOT", TenantId = null };
        role.SetId(Guid.NewGuid());
        roles.Add(role);
        return role;
    }

    [Fact]
    public async Task CreateAsync_stores_the_role_at_the_given_level()
    {
        var id = await sut.CreateAsync("Admin", RoleLevel.Level2, "Administrators");

        Assert.NotNull(id);
        var role = Assert.Single(roles);
        Assert.Equal(RoleLevel.Level2, role.Level);
        Assert.Equal("ADMIN", role.NormalizedName);
    }

    [Fact]
    public async Task CreateAsync_returns_null_for_a_duplicate_name()
    {
        await sut.CreateAsync("Admin", RoleLevel.Level2);
        Assert.Null(await sut.CreateAsync("admin", RoleLevel.Level3));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_level_outside_the_enum()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.CreateAsync("Bad", (RoleLevel)9));
    }

    [Fact]
    public async Task SetLevelAsync_changes_the_level_of_a_role_in_scope()
    {
        var id = (await sut.CreateAsync("Staff", RoleLevel.Level5))!.Value;

        Assert.True(await sut.SetLevelAsync(id, RoleLevel.Level3));

        Assert.Equal(RoleLevel.Level3, Assert.Single(roles).Level);
    }

    [Fact]
    public async Task SetLevelAsync_returns_false_for_a_role_in_another_tenant()
    {
        var other = new Role { Name = "x", NormalizedName = "X", TenantId = new TenantId("other") };
        other.SetId(Guid.NewGuid());
        roles.Add(other);

        Assert.False(await sut.SetLevelAsync(other.Id, RoleLevel.Level1));
        Assert.Equal(RoleLevel.Level5, other.Level);
    }

    [Fact]
    public async Task SetLevelAsync_rejects_a_level_outside_the_enum()
    {
        var id = (await sut.CreateAsync("Staff", RoleLevel.Level5))!.Value;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sut.SetLevelAsync(id, (RoleLevel)0));
    }

    [Fact]
    public async Task SetLevelAsync_refuses_a_platform_role_from_a_tenant_scope()
    {
        var platformRole = SeedPlatformRole();

        Assert.False(await sut.SetLevelAsync(platformRole.Id, RoleLevel.Level1));

        Assert.Equal(RoleLevel.Level5, platformRole.Level);
    }

    [Fact]
    public async Task SetLevelAsync_changes_a_platform_role_from_platform_scope()
    {
        var platformRole = SeedPlatformRole();

        Assert.True(await platformSut.SetLevelAsync(platformRole.Id, RoleLevel.Level1));

        Assert.Equal(RoleLevel.Level1, platformRole.Level);
    }
}
