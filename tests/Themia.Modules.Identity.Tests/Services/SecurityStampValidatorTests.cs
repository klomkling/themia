using Microsoft.Extensions.Caching.Memory;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class SecurityStampValidatorTests
{
    private readonly List<User> store = [];
    private readonly FakeRepository<User> repo;
    private readonly IdentityModuleOptions options = new() { SecurityStampCacheDuration = TimeSpan.Zero };
    private readonly MemoryCache cache = new(new MemoryCacheOptions());

    public SecurityStampValidatorTests()
    {
        repo = new FakeRepository<User>(store, u => u.Id) { AmbientTenant = null };
    }

    private SecurityStampValidator CreateSut() => new(repo, cache, options);

    private User Seed(TenantId? tenant = null, bool active = true)
    {
        var user = new User { UserName = "u", NormalizedUserName = "U", TenantId = tenant, IsActive = active };
        user.SetId(Guid.NewGuid());
        store.Add(user);
        return user;
    }

    [Fact]
    public async Task IsCurrentAsync_returns_true_for_the_stored_stamp_of_an_active_user()
    {
        var user = Seed();
        Assert.True(await CreateSut().IsCurrentAsync(user.Id, user.SecurityStamp));
    }

    [Fact]
    public async Task IsCurrentAsync_returns_false_when_the_stamp_differs()
    {
        var user = Seed();
        Assert.False(await CreateSut().IsCurrentAsync(user.Id, "old-stamp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task IsCurrentAsync_returns_false_when_the_token_carries_no_stamp(string? stamp)
    {
        var user = Seed();
        Assert.False(await CreateSut().IsCurrentAsync(user.Id, stamp));
    }

    [Fact]
    public async Task IsCurrentAsync_returns_false_for_an_inactive_user()
    {
        var user = Seed(active: false);
        Assert.False(await CreateSut().IsCurrentAsync(user.Id, user.SecurityStamp));
    }

    [Fact]
    public async Task IsCurrentAsync_returns_false_for_a_deleted_user()
    {
        var user = Seed();
        user.IsDeleted = true;
        Assert.False(await CreateSut().IsCurrentAsync(user.Id, user.SecurityStamp));
    }

    [Fact]
    public async Task IsCurrentAsync_returns_false_for_an_unknown_user()
    {
        Assert.False(await CreateSut().IsCurrentAsync(Guid.NewGuid(), "any"));
    }

    [Fact]
    public async Task IsCurrentAsync_finds_a_tenant_user_with_no_ambient_tenant()
    {
        // The bearer check runs before tenant resolution, so the lookup must not depend on the ambient tenant.
        var user = Seed(new TenantId("acme"));
        Assert.True(await CreateSut().IsCurrentAsync(user.Id, user.SecurityStamp));
    }

    [Fact]
    public async Task IsCurrentAsync_reads_the_store_on_every_call_when_the_cache_is_disabled()
    {
        var user = Seed();
        var sut = CreateSut();
        var issued = user.SecurityStamp;
        Assert.True(await sut.IsCurrentAsync(user.Id, issued));

        user.SecurityStamp = "rotated";

        Assert.False(await sut.IsCurrentAsync(user.Id, issued));
    }

    [Fact]
    public async Task IsCurrentAsync_serves_the_cached_state_within_the_cache_window()
    {
        options.SecurityStampCacheDuration = TimeSpan.FromMinutes(1);
        var user = Seed();
        var sut = CreateSut();
        var issued = user.SecurityStamp;
        Assert.True(await sut.IsCurrentAsync(user.Id, issued));

        user.SecurityStamp = "rotated";

        Assert.True(await sut.IsCurrentAsync(user.Id, issued));   // stale by design, bounded by the window
    }

    [Fact]
    public async Task IsCurrentAsync_caches_a_refusal_too()
    {
        options.SecurityStampCacheDuration = TimeSpan.FromMinutes(1);
        var user = Seed(active: false);
        var sut = CreateSut();
        Assert.False(await sut.IsCurrentAsync(user.Id, user.SecurityStamp));

        user.IsActive = true;

        Assert.False(await sut.IsCurrentAsync(user.Id, user.SecurityStamp));
    }
}
