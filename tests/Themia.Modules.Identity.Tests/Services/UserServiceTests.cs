using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
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
        sut = new UserService(repo, uow, new Argon2idPasswordHasher(), clock, options, new DataFilterScope());
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

    [Fact]
    public async Task VerifyPasswordAsync_resets_failure_count_after_successful_login()
    {
        options.MaxFailedAccessAttempts = 3;
        await sut.CreateAsync("ivy", "right");

        Assert.Equal(PasswordVerificationResult.Failed, await sut.VerifyPasswordAsync("ivy", "wrong"));
        Assert.Equal(PasswordVerificationResult.Failed, await sut.VerifyPasswordAsync("ivy", "wrong"));

        // Successful login resets the failure counter.
        Assert.Equal(PasswordVerificationResult.Success, await sut.VerifyPasswordAsync("ivy", "right"));
        Assert.Equal(0, Assert.Single(store).AccessFailedCount);

        // One more failure must report Failed, not LockedOut — proving the counter reset.
        Assert.Equal(PasswordVerificationResult.Failed, await sut.VerifyPasswordAsync("ivy", "wrong"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_rehashes_and_rotates_stamp_when_hash_is_outdated()
    {
        var stub = new StubPasswordHasher { VerifyResult = true, NeedsRehashResult = true, HashResult = "rehashed" };
        var service = new UserService(repo, uow, stub, clock, options, new DataFilterScope());

        // Seed with a distinct, outdated hash so the rehash to the "rehashed" sentinel is observable.
        var seeded = new User { UserName = "jane", NormalizedUserName = "JANE", PasswordHash = "outdated", TenantId = new TenantId("acme") };
        seeded.SetId(Guid.NewGuid());
        store.Add(seeded);
        var stampBefore = seeded.SecurityStamp;

        Assert.Equal(PasswordVerificationResult.Success, await service.VerifyPasswordAsync("jane", "pw"));

        var after = Assert.Single(store);
        Assert.Equal("rehashed", after.PasswordHash);
        Assert.NotEqual(stampBefore, after.SecurityStamp);
    }

    [Fact]
    public async Task VerifyPasswordAsync_never_locks_out_when_lockout_disabled()
    {
        options.MaxFailedAccessAttempts = 1;
        await sut.CreateAsync("kyle", "right");
        Assert.Single(store).LockoutEnabled = false;

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(PasswordVerificationResult.Failed, await sut.VerifyPasswordAsync("kyle", "wrong"));
        }

        Assert.Null(Assert.Single(store).LockoutEnd);
    }

    [Fact]
    public async Task SetPasswordAsync_rotates_security_stamp_and_new_password_verifies()
    {
        var create = await sut.CreateAsync("liam", "oldpw");
        var stampBefore = Assert.Single(store).SecurityStamp;

        Assert.True(await sut.SetPasswordAsync(create.UserId!.Value, "newpw"));

        Assert.NotEqual(stampBefore, Assert.Single(store).SecurityStamp);
        Assert.Equal(PasswordVerificationResult.Success, await sut.VerifyPasswordAsync("liam", "newpw"));
    }

    [Fact]
    public async Task Platform_user_lockout_write_succeeds()
    {
        // A platform user (TenantId null) seen from a tenant scope (ambient "acme"). A failed
        // verification writes the lockout counter on a global ITenantEntity — under the real
        // unit of work that needs the filter bypass; here we guard the resolution/no-throw path.
        options.MaxFailedAccessAttempts = 3;
        var platform = new User { UserName = "root", NormalizedUserName = "ROOT", PasswordHash = "x", TenantId = null };
        platform.SetId(Guid.NewGuid());
        store.Add(platform);

        var result = await sut.VerifyPasswordAsync("root", "wrong");

        Assert.Equal(PasswordVerificationResult.Failed, result);
    }

    [Fact]
    public async Task SetActiveAsync_resolves_platform_user()
    {
        var platform = new User { UserName = "root", NormalizedUserName = "ROOT", PasswordHash = "x", TenantId = null };
        platform.SetId(Guid.NewGuid());
        store.Add(platform);

        Assert.True(await sut.SetActiveAsync(platform.Id, false));
        Assert.False(Assert.Single(store).IsActive);
    }

    /// <summary>Minimal hasher to drive the rehash path deterministically.</summary>
    private sealed class StubPasswordHasher : IPasswordHasher
    {
        public string HashResult { get; init; } = "hashed";
        public bool VerifyResult { get; init; }
        public bool NeedsRehashResult { get; init; }

        public string Hash(string password) => HashResult;
        public bool Verify(string encodedHash, string password) => VerifyResult;
        public bool NeedsRehash(string encodedHash) => NeedsRehashResult;
    }
}
