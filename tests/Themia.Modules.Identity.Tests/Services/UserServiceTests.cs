using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class UserServiceTests
{
    private readonly List<User> store = [];
    private readonly FakeRepository<User> repo;
    private readonly FakeUnitOfWork uow = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-14T00:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly UserService sut;

    public UserServiceTests()
    {
        repo = new FakeRepository<User>(store, u => u.Id) { AmbientTenant = new TenantId("acme") };
        sut = new UserService(repo, uow, new Argon2idPasswordHasher(), clock, options);
    }

    [Fact]
    public async Task CreateAsync_persists_normalized_user_with_hashed_password()
    {
        var result = await sut.CreateAsync("Alice", "pw1", "Alice@Example.com");

        Assert.True(result.Succeeded);
        var user = Assert.Single(store);
        Assert.Equal("ALICE", user.NormalizedUserName);
        Assert.Equal("ALICE@EXAMPLE.COM", user.NormalizedEmail);
        Assert.NotEqual("pw1", user.PasswordHash);
        Assert.Equal(new TenantId("acme"), user.TenantId);   // stamped by the repo
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_user_name_in_same_tenant()
    {
        await sut.CreateAsync("bob", "pw");
        var second = await sut.CreateAsync("BOB", "pw");

        Assert.False(second.Succeeded);
        Assert.Equal("duplicate_user_name", second.Error);
    }

    [Fact]
    public async Task FindByUserNameAsync_finds_tenant_user_case_insensitively()
    {
        await sut.CreateAsync("carol", "pw");
        var found = await sut.FindByUserNameAsync("CAROL");
        Assert.NotNull(found);
    }

    [Fact]
    public async Task FindByUserNameAsync_falls_back_to_platform_user()
    {
        // A platform user (TenantId null) created directly in the store.
        var platform = new User { UserName = "root", NormalizedUserName = "ROOT", PasswordHash = "x", TenantId = null };
        platform.SetId(Guid.NewGuid());
        store.Add(platform);

        var found = await sut.FindByUserNameAsync("root");
        Assert.NotNull(found);
        Assert.Null(found!.TenantId);
    }

    [Fact]
    public async Task FindByUserNameAsync_skips_platform_when_disabled()
    {
        options.AllowPlatformLogin = false;
        var platform = new User { UserName = "root", NormalizedUserName = "ROOT", PasswordHash = "x", TenantId = null };
        platform.SetId(Guid.NewGuid());
        store.Add(platform);

        Assert.Null(await sut.FindByUserNameAsync("root"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_succeeds_for_correct_password()
    {
        await sut.CreateAsync("dave", "secret");
        Assert.Equal(PasswordVerificationResult.Success, await sut.VerifyPasswordAsync("dave", "secret"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_reports_not_found_for_unknown_user()
    {
        Assert.Equal(PasswordVerificationResult.NotFound, await sut.VerifyPasswordAsync("ghost", "x"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_locks_out_after_threshold_then_reports_locked()
    {
        options.MaxFailedAccessAttempts = 3;
        await sut.CreateAsync("erin", "right");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(PasswordVerificationResult.Failed, await sut.VerifyPasswordAsync("erin", "wrong"));
        }

        // Threshold reached: even the correct password is refused while locked out.
        Assert.Equal(PasswordVerificationResult.LockedOut, await sut.VerifyPasswordAsync("erin", "right"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_unlocks_after_lockout_window()
    {
        options.MaxFailedAccessAttempts = 1;
        options.LockoutDuration = TimeSpan.FromMinutes(10);
        await sut.CreateAsync("frank", "right");

        Assert.Equal(PasswordVerificationResult.Failed, await sut.VerifyPasswordAsync("frank", "wrong"));
        Assert.Equal(PasswordVerificationResult.LockedOut, await sut.VerifyPasswordAsync("frank", "right"));

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(PasswordVerificationResult.Success, await sut.VerifyPasswordAsync("frank", "right"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_reports_inactive_for_disabled_account()
    {
        var create = await sut.CreateAsync("gina", "pw");
        await sut.SetActiveAsync(create.UserId!.Value, false);
        Assert.Equal(PasswordVerificationResult.Inactive, await sut.VerifyPasswordAsync("gina", "pw"));
    }

    [Fact]
    public async Task DeleteAsync_soft_deletes_so_lookup_no_longer_finds_the_user()
    {
        var create = await sut.CreateAsync("hank", "pw");
        Assert.True(await sut.DeleteAsync(create.UserId!.Value));
        Assert.Null(await sut.FindByUserNameAsync("hank"));
    }
}
