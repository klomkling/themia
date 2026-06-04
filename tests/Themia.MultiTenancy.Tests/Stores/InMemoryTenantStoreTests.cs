using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.Stores;

namespace Themia.MultiTenancy.Tests.Stores;

public class InMemoryTenantStoreTests
{
    [Fact]
    public async Task FindByIdentifierAsync_WithExistingTenant_ShouldReturnTenant()
    {
        var tenant = new TenantInfo("tenant-1", "acme", "Acme Corp");
        var store = new InMemoryTenantStore(new[] { tenant });

        var result = await store.FindByIdentifierAsync("acme");

        Assert.NotNull(result);
        Assert.Equal("tenant-1", result.Id);
        Assert.Equal("acme", result.Identifier);
        Assert.Equal("Acme Corp", result.Name);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithNonExistingTenant_ShouldReturnNull()
    {
        var store = new InMemoryTenantStore();

        var result = await store.FindByIdentifierAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task FindByIdentifierAsync_CaseInsensitive_ShouldReturnTenant()
    {
        var tenant = new TenantInfo("tenant-1", "acme");
        var store = new InMemoryTenantStore(new[] { tenant });

        var result = await store.FindByIdentifierAsync("ACME");

        Assert.NotNull(result);
        Assert.Equal("acme", result.Identifier);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithMultipleTenants_ShouldReturnCorrectTenant()
    {
        var tenant1 = new TenantInfo("tenant-1", "acme", "Acme Corp");
        var tenant2 = new TenantInfo("tenant-2", "globex", "Globex Corp");
        var tenant3 = new TenantInfo("tenant-3", "initech", "Initech Corp");
        var store = new InMemoryTenantStore(new[] { tenant1, tenant2, tenant3 });

        var result = await store.FindByIdentifierAsync("globex");

        Assert.NotNull(result);
        Assert.Equal("tenant-2", result.Id);
        Assert.Equal("Globex Corp", result.Name);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithCancellationToken_ShouldRespectCancellation()
    {
        var store = new InMemoryTenantStore();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await store.FindByIdentifierAsync("acme", cts.Token);
        });
    }

    [Fact]
    public async Task Constructor_WithNoTenants_ShouldCreateEmptyStore()
    {
        var store = new InMemoryTenantStore();

        var result = await store.FindByIdentifierAsync("any");

        Assert.Null(result);
    }

    [Fact]
    public async Task Constructor_WithSeedTenants_ShouldPopulateStore()
    {
        var tenants = new[]
        {
            new TenantInfo("tenant-1", "acme"),
            new TenantInfo("tenant-2", "globex"),
            new TenantInfo("tenant-3", "initech")
        };
        var store = new InMemoryTenantStore(tenants);

        var result1 = await store.FindByIdentifierAsync("acme");
        var result2 = await store.FindByIdentifierAsync("globex");
        var result3 = await store.FindByIdentifierAsync("initech");

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.NotNull(result3);
    }

    [Theory]
    [InlineData("tenant-with-dashes")]
    [InlineData("tenant_with_underscores")]
    [InlineData("123-numeric")]
    [InlineData("UPPERCASE")]
    public async Task FindByIdentifierAsync_WithVariousIdentifierFormats_ShouldWork(string identifier)
    {
        var tenant = new TenantInfo("tenant-1", identifier);
        var store = new InMemoryTenantStore(new[] { tenant });

        var result = await store.FindByIdentifierAsync(identifier);

        Assert.NotNull(result);
        Assert.Equal(identifier, result.Identifier);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithUnicodeIdentifier_ShouldWork()
    {
        var tenant = new TenantInfo("tenant-1", "テナント");
        var store = new InMemoryTenantStore(new[] { tenant });

        var result = await store.FindByIdentifierAsync("テナント");

        Assert.NotNull(result);
        Assert.Equal("テナント", result.Identifier);
    }

    [Fact]
    public async Task FindByIdentifierAsync_MultipleCalls_ShouldReturnSameInstance()
    {
        var tenant = new TenantInfo("tenant-1", "acme");
        var store = new InMemoryTenantStore(new[] { tenant });

        var result1 = await store.FindByIdentifierAsync("acme");
        var result2 = await store.FindByIdentifierAsync("acme");

        Assert.Equal(result2, result1);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithDuplicateIdentifiers_ShouldUseLastOne()
    {
        // When duplicates exist, ConcurrentDictionary will use the last one
        var tenant1 = new TenantInfo("tenant-1", "acme", "First Acme");
        var tenant2 = new TenantInfo("tenant-2", "acme", "Second Acme");
        var store = new InMemoryTenantStore(new[] { tenant1, tenant2 });

        var result = await store.FindByIdentifierAsync("acme");

        Assert.NotNull(result);
        Assert.Equal("tenant-2", result.Id);
        Assert.Equal("Second Acme", result.Name);
    }
}
