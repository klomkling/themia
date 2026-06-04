using Microsoft.Extensions.Caching.Memory;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Stores;
using Themia.MultiTenancy.Tests.TestUtilities;

namespace Themia.MultiTenancy.Tests.Stores;

public class CachedTenantStoreTests
{
    [Fact]
    public async Task FindByIdentifierAsync_FirstCall_ShouldHitInnerStore()
    {
        var tenant = new TenantInfo("tenant-1", "acme");
        var innerStore = new FakeTenantStore(tenant);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedStore = new CachedTenantStore(innerStore, cache);

        var result = await cachedStore.FindByIdentifierAsync("acme");

        Assert.NotNull(result);
        Assert.Equal("acme", result.Identifier);
        Assert.Equal(1, innerStore.FindCallCount);
    }

    [Fact]
    public async Task FindByIdentifierAsync_SecondCall_ShouldUseCache()
    {
        var tenant = new TenantInfo("tenant-1", "acme");
        var innerStore = new FakeTenantStore(tenant);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedStore = new CachedTenantStore(innerStore, cache);

        await cachedStore.FindByIdentifierAsync("acme");
        var result = await cachedStore.FindByIdentifierAsync("acme");

        Assert.NotNull(result);
        Assert.Equal(1, innerStore.FindCallCount); // Only called once
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithNullIdentifier_ShouldThrowArgumentException()
    {
        var innerStore = new FakeTenantStore();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedStore = new CachedTenantStore(innerStore, cache);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await cachedStore.FindByIdentifierAsync(null!);
        });
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithEmptyIdentifier_ShouldThrowArgumentException()
    {
        var innerStore = new FakeTenantStore();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedStore = new CachedTenantStore(innerStore, cache);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await cachedStore.FindByIdentifierAsync("");
        });
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithWhitespaceIdentifier_ShouldThrowArgumentException()
    {
        var innerStore = new FakeTenantStore();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedStore = new CachedTenantStore(innerStore, cache);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await cachedStore.FindByIdentifierAsync("   ");
        });
    }

    [Fact]
    public async Task FindByIdentifierAsync_DifferentIdentifiers_ShouldCacheSeparately()
    {
        var tenant1 = new TenantInfo("tenant-1", "acme");
        var tenant2 = new TenantInfo("tenant-2", "globex");
        var innerStore = new FakeTenantStore(tenant1, tenant2);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedStore = new CachedTenantStore(innerStore, cache);

        await cachedStore.FindByIdentifierAsync("acme");
        await cachedStore.FindByIdentifierAsync("globex");
        await cachedStore.FindByIdentifierAsync("acme");
        await cachedStore.FindByIdentifierAsync("globex");

        Assert.Equal(2, innerStore.FindCallCount); // One call per unique identifier
    }

    [Fact]
    public async Task FindByIdentifierAsync_NotFoundInStore_ShouldNotCacheNull()
    {
        var innerStore = new FakeTenantStore();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedStore = new CachedTenantStore(innerStore, cache);

        var result1 = await cachedStore.FindByIdentifierAsync("nonexistent");
        var result2 = await cachedStore.FindByIdentifierAsync("nonexistent");

        Assert.Null(result1);
        Assert.Null(result2);
        Assert.Equal(2, innerStore.FindCallCount); // Not cached, calls inner store each time
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithTTL_ShouldExpireAfterTimeout()
    {
        var tenant = new TenantInfo("tenant-1", "acme");
        var innerStore = new FakeTenantStore(tenant);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var ttl = TimeSpan.FromMilliseconds(50);
        var cachedStore = new CachedTenantStore(innerStore, cache, ttl);

        await cachedStore.FindByIdentifierAsync("acme");
        await Task.Delay(100); // Wait for cache to expire
        await cachedStore.FindByIdentifierAsync("acme");

        Assert.Equal(2, innerStore.FindCallCount); // Called twice due to expiration
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithCancellationToken_ShouldRespectCancellation()
    {
        var innerStore = new FakeTenantStore();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedStore = new CachedTenantStore(innerStore, cache);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await cachedStore.FindByIdentifierAsync("acme", cts.Token);
        });
    }

    [Fact]
    public void Constructor_WithNullInnerStore_ShouldThrowArgumentNullException()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());

        Assert.Throws<ArgumentNullException>(() =>
        {
            new CachedTenantStore(null!, cache);
        });
    }

    [Fact]
    public void Constructor_WithNullCache_ShouldThrowArgumentNullException()
    {
        var innerStore = new FakeTenantStore();

        Assert.Throws<ArgumentNullException>(() =>
        {
            new CachedTenantStore(innerStore, null!);
        });
    }

    [Fact]
    public async Task FindByIdentifierAsync_ConcurrentCalls_ShouldHandleGracefully()
    {
        var tenant = new TenantInfo("tenant-1", "acme");
        var innerStore = new FakeTenantStore(tenant);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cachedStore = new CachedTenantStore(innerStore, cache);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => cachedStore.FindByIdentifierAsync("acme"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r != null && r.Identifier == "acme"));
        // First call(s) might hit the inner store before cache is populated
        Assert.True(innerStore.FindCallCount > 0);
        Assert.True(innerStore.FindCallCount <= 10);
    }
}
