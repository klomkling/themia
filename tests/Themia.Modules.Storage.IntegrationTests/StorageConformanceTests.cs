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

/// <summary>Wraps a real provider but throws on the Nth <see cref="PutAsync"/> (1-based) so the
/// metadata-first compensation path can be exercised. Reads/deletes delegate to the inner provider.</summary>
file sealed class ThrowingStorageProvider(IStorageProvider inner, int throwOnPutNumber) : IStorageProvider
{
    private int puts;

    public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref puts) == throwOnPutNumber)
        {
            throw new InvalidOperationException("Simulated backend write failure.");
        }

        return inner.PutAsync(key, content, options, cancellationToken);
    }

    public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        inner.GetAsync(key, cancellationToken);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(key, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(key, cancellationToken);

    public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default) =>
        inner.GetPresignedUrlAsync(key, request, cancellationToken);
}

/// <summary>An <see cref="IFileScanner"/> that always reports the content as a threat.</summary>
file sealed class RejectingScanner : Themia.Modules.Storage.Scanning.IFileScanner
{
    public Task<Themia.Modules.Storage.Scanning.FileScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Themia.Modules.Storage.Scanning.FileScanResult(false, "EICAR"));
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

    protected Scope NewScope(
        TenantId? tenant,
        long quota = 1_000_000,
        string localRoot = "",
        Action<StorageModuleOptions>? configureOptions = null,
        Action<IServiceCollection>? configureServices = null,
        Func<IStorageProvider, IStorageProvider>? providerOverride = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        ConfigurePeer(services, configuration);
        var builder = services.AddThemiaStorage(o =>
            {
                o.DefaultTenantQuotaBytes = quota;
                configureOptions?.Invoke(o);
            })
            .UseLocal(o =>
            {
                o.RootPath = string.IsNullOrEmpty(localRoot)
                    ? Path.Combine(Path.GetTempPath(), "themia-storage-it", Guid.NewGuid().ToString("N"))
                    : localRoot;
                o.SigningKey = "integration-signing-key-please-change!";
            });

        configureServices?.Invoke(services);

        // Replace the registered backend with a wrapper (e.g. a throwing fake) so compensation/best-effort
        // paths can be exercised against the real EF/Dapper repo + UoW.
        if (providerOverride is not null)
        {
            var descriptor = builder.Services.Last(d => d.ServiceType == typeof(IStorageProvider));
            Func<IServiceProvider, IStorageProvider> factory = descriptor.ImplementationFactory is { } f
                ? sp => (IStorageProvider)f(sp)
                : _ => (IStorageProvider)descriptor.ImplementationInstance!;
            services.RemoveAll<IStorageProvider>();
            services.AddSingleton<IStorageProvider>(sp => providerOverride(factory(sp)));
        }

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
    public async Task Platform_object_invisible_from_tenant_scope()
    {
        // A platform (null-tenant) object must never resolve from a tenant scope, even over the same
        // DB + blob root. Guards against EF's IncludeGlobalRecordsForTenants leaking platform rows.
        await ResetAsync();
        var sharedRoot = Path.Combine(Path.GetTempPath(), "themia-storage-it", Guid.NewGuid().ToString("N"));
        await using (var platform = NewScope(tenant: null, localRoot: sharedRoot))
        {
            await platform.Storage.PutAsync("p.txt", Bytes("plat"), new StoragePutOptions("text/plain"));
        }
        await using (var tenant = NewScope(new TenantId("acme"), localRoot: sharedRoot))
        {
            Assert.False(await tenant.Storage.ExistsAsync("p.txt"));
            Assert.Null(await tenant.Storage.GetAsync("p.txt"));
        }
    }

    [Fact]
    public async Task Platform_object_does_not_count_against_tenant_quota()
    {
        // Platform bytes must not be charged to a tenant's quota: the tenant can still fill its own.
        await ResetAsync();
        var sharedRoot = Path.Combine(Path.GetTempPath(), "themia-storage-it", Guid.NewGuid().ToString("N"));
        await using (var platform = NewScope(tenant: null, localRoot: sharedRoot))
        {
            await platform.Storage.PutAsync("big.txt", Bytes("0123456789"), new StoragePutOptions("text/plain")); // 10 bytes
        }
        await using (var tenant = NewScope(new TenantId("acme"), quota: 8, localRoot: sharedRoot))
        {
            // Tenant's own quota is 8; the 10-byte platform object is not charged, so this 5-byte Put fits.
            var stored = await tenant.Storage.PutAsync("a.txt", Bytes("12345"), new StoragePutOptions("text/plain"));
            Assert.Equal(5, stored.SizeBytes);
        }
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

    [Fact]
    public async Task Failed_blob_write_on_new_key_rolls_back_reservation_and_frees_quota()
    {
        // A new-key Put whose blob write throws must roll back the reservation row entirely: the key
        // must not exist afterward, and the reserved bytes must not be charged against the quota.
        await ResetAsync();
        var sharedRoot = Path.Combine(Path.GetTempPath(), "themia-storage-it", Guid.NewGuid().ToString("N"));

        await using (var failing = NewScope(new TenantId("acme"), quota: 8, localRoot: sharedRoot,
            providerOverride: inner => new ThrowingStorageProvider(inner, throwOnPutNumber: 1)))
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => failing.Storage.PutAsync("k.txt", Bytes("12345"), new StoragePutOptions("text/plain")));
        }

        await using (var s = NewScope(new TenantId("acme"), quota: 8, localRoot: sharedRoot))
        {
            Assert.False(await s.Storage.ExistsAsync("k.txt"));      // reservation rolled back
            // Quota freed: a fresh 5-byte Put fits under the tight 8-byte quota.
            var stored = await s.Storage.PutAsync("k.txt", Bytes("12345"), new StoragePutOptions("text/plain"));
            Assert.Equal(5, stored.SizeBytes);
        }
    }

    [Fact]
    public async Task Failed_blob_write_on_overwrite_preserves_original_metadata()
    {
        // Seed an object (Put #1 succeeds), then a second Put whose blob write throws (Put #2). The
        // original object's metadata must be preserved and it must remain readable (its blob is intact).
        await ResetAsync();
        var sharedRoot = Path.Combine(Path.GetTempPath(), "themia-storage-it", Guid.NewGuid().ToString("N"));

        await using var s = NewScope(new TenantId("acme"), localRoot: sharedRoot,
            providerOverride: inner => new ThrowingStorageProvider(inner, throwOnPutNumber: 2));

        var original = await s.Storage.PutAsync("k.txt", Bytes("hello"), new StoragePutOptions("text/plain"));
        Assert.Equal(5, original.SizeBytes);

        await Assert.ThrowsAnyAsync<Exception>(
            () => s.Storage.PutAsync("k.txt", Bytes("123456789"), new StoragePutOptions("application/json")));

        // Metadata unchanged from the original write.
        var read = await s.Storage.GetAsync("k.txt");
        Assert.NotNull(read);
        Assert.Equal("text/plain", read!.ContentType);
        using var reader = new StreamReader(read.Content);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Disallowed_content_type_is_rejected_and_persists_nothing()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"),
            configureOptions: o => o.AllowedContentTypes = ["text/plain"]);

        await Assert.ThrowsAsync<StorageValidationException>(
            () => s.Storage.PutAsync("k.txt", Bytes("data"), new StoragePutOptions("application/json")));
        Assert.False(await s.Storage.ExistsAsync("k.txt"));
    }

    [Fact]
    public async Task Scan_rejection_throws_and_persists_nothing()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"), configureServices: services =>
        {
            services.RemoveAll<Themia.Modules.Storage.Scanning.IFileScanner>();
            services.AddSingleton<Themia.Modules.Storage.Scanning.IFileScanner, RejectingScanner>();
        });

        await Assert.ThrowsAsync<StorageScanException>(
            () => s.Storage.PutAsync("k.txt", Bytes("data"), new StoragePutOptions("text/plain")));
        Assert.False(await s.Storage.ExistsAsync("k.txt"));
    }

    [Fact]
    public async Task Oversize_stream_is_rejected_by_the_buffer_cap_and_persists_nothing()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"), configureOptions: o => o.MaxObjectSizeBytes = 4);

        await Assert.ThrowsAsync<StorageValidationException>(
            () => s.Storage.PutAsync("k.txt", Bytes("0123456789"), new StoragePutOptions("text/plain")));
        Assert.False(await s.Storage.ExistsAsync("k.txt"));
    }
}
