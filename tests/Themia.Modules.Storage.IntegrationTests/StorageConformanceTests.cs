using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Storage;
using Themia.Modules.Storage.DependencyInjection;
using Themia.Storage;
using Xunit;

namespace Themia.Modules.Storage.IntegrationTests;

file sealed class StubCurrentUserAccessor(string? userId) : ICurrentUserAccessor
{
    public string? UserId { get; } = userId;
}

public abstract class StorageConformanceTests
{
    protected abstract void ConfigurePeer(IServiceCollection services, IConfiguration configuration);
    protected abstract Task ResetAsync();
    protected abstract string ConnectionString { get; }

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    protected sealed record Scope(ServiceProvider Provider, AsyncServiceScope Inner) : IAsyncDisposable
    {
        public ITenantStorage Storage => Inner.ServiceProvider.GetRequiredService<ITenantStorage>();
        public async ValueTask DisposeAsync() { await Inner.DisposeAsync(); await Provider.DisposeAsync(); }
    }

    protected Scope NewScope(TenantId? tenant, long quota = 1_000_000, string localRoot = "")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        ConfigurePeer(services, configuration);
        services.AddThemiaStorage(o => o.DefaultTenantQuotaBytes = quota)
            .UseLocal(o =>
            {
                o.RootPath = string.IsNullOrEmpty(localRoot)
                    ? Path.Combine(Path.GetTempPath(), "themia-storage-it", Guid.NewGuid().ToString("N"))
                    : localRoot;
                o.SigningKey = "integration-signing-key-please-change!";
            });

        services.RemoveAll<ICurrentUserAccessor>();
        services.AddSingleton<ICurrentUserAccessor>(new StubCurrentUserAccessor("test-user"));

        var provider = services.BuildServiceProvider();
        return new Scope(provider, provider.CreateAsyncScope());
    }

    [Fact]
    public async Task Put_get_delete_round_trip()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var stored = await s.Storage.PutAsync("docs/a.txt", Bytes("hello"), new StoragePutOptions("text/plain"));
        Assert.Equal("docs/a.txt", stored.Key);
        Assert.Equal(5, stored.SizeBytes);

        var read = await s.Storage.GetAsync("docs/a.txt");
        Assert.NotNull(read);
        using (var reader = new StreamReader(read!.Content)) Assert.Equal("hello", await reader.ReadToEndAsync());

        await s.Storage.DeleteAsync("docs/a.txt");
        Assert.False(await s.Storage.ExistsAsync("docs/a.txt"));
        Assert.Null(await s.Storage.GetAsync("docs/a.txt"));
    }

    [Fact]
    public async Task Objects_are_tenant_isolated()
    {
        await ResetAsync();
        var sharedRoot = Path.Combine(Path.GetTempPath(), "themia-storage-it", Guid.NewGuid().ToString("N"));
        await using (var a = NewScope(new TenantId("a"), localRoot: sharedRoot))
        {
            await a.Storage.PutAsync("k.txt", Bytes("a-data"), new StoragePutOptions("text/plain"));
        }
        await using (var b = NewScope(new TenantId("b"), localRoot: sharedRoot))
        {
            Assert.False(await b.Storage.ExistsAsync("k.txt"));      // same key, different tenant → invisible
            Assert.Null(await b.Storage.GetAsync("k.txt"));
        }
    }

    [Fact]
    public async Task Quota_is_enforced()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"), quota: 8);
        await s.Storage.PutAsync("a.txt", Bytes("12345"), new StoragePutOptions("text/plain")); // 5 bytes, ok
        await Assert.ThrowsAsync<StorageQuotaExceededException>(
            () => s.Storage.PutAsync("b.txt", Bytes("12345"), new StoragePutOptions("text/plain"))); // +5 > 8
    }

    [Fact]
    public async Task Overwrite_replaces_size_not_doubles_usage()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"), quota: 8);
        await s.Storage.PutAsync("a.txt", Bytes("12345"), new StoragePutOptions("text/plain")); // 5
        var stored = await s.Storage.PutAsync("a.txt", Bytes("123"), new StoragePutOptions("text/plain")); // replace → 3
        Assert.Equal(3, stored.SizeBytes); // usage 3, not 8
    }

    [Fact]
    public async Task Platform_object_round_trips()
    {
        await ResetAsync();
        await using var s = NewScope(tenant: null);
        await s.Storage.PutAsync("p.txt", Bytes("plat"), new StoragePutOptions("text/plain"));
        Assert.True(await s.Storage.ExistsAsync("p.txt"));
    }

    [Fact]
    public async Task Reupload_after_delete_same_key_succeeds()
    {
        // The filtered unique index excludes soft-deleted rows, so a deleted key can be re-uploaded
        // without hitting the (tenant_id, key) unique constraint.
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        await s.Storage.PutAsync("k.txt", Bytes("v1"), new StoragePutOptions("text/plain"));
        await s.Storage.DeleteAsync("k.txt");

        var stored = await s.Storage.PutAsync("k.txt", Bytes("v2-longer"), new StoragePutOptions("text/plain"));
        Assert.Equal("k.txt", stored.Key);
        var read = await s.Storage.GetAsync("k.txt");
        Assert.NotNull(read);
        using var reader = new StreamReader(read!.Content);
        Assert.Equal("v2-longer", await reader.ReadToEndAsync());
    }
}
