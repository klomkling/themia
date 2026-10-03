using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Themia.Storage.Urls;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>Registering the router, and IStorageUrlService resolving both kinds of link over it (coord #0152).</summary>
public sealed class SplitStorageRegistrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-split-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_instance_form_registers_the_router_as_the_single_IStorageProvider()
    {
        var services = new ServiceCollection();

        services.AddThemiaSplitStorage(new SpyStorageProvider("public", hasPublicContainer: true), new SpyStorageProvider("private"));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<SplitStorageProvider>(provider.GetRequiredService<IStorageProvider>());
    }

    [Fact]
    public void The_instance_form_throws_when_a_storage_provider_is_already_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStorageProvider>(new SpyStorageProvider("single"));

        Assert.Throws<InvalidOperationException>(() => services.AddThemiaSplitStorage(
            new SpyStorageProvider("public", hasPublicContainer: true), new SpyStorageProvider("private")));
    }

    [Fact]
    public void The_instance_form_validates_at_registration_so_a_bad_pair_fails_before_the_host_runs()
    {
        var services = new ServiceCollection();
        var sameInstance = new SpyStorageProvider("both", hasPublicContainer: true);

        Assert.Throws<ArgumentException>(() => services.AddThemiaSplitStorage(sameInstance, sameInstance));
    }

    [Fact]
    public void The_factory_form_builds_the_slots_from_the_container_on_first_resolve()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new SpyStorageProvider("public", hasPublicContainer: true));
        services.AddThemiaSplitStorage(
            sp => sp.GetRequiredService<SpyStorageProvider>(),
            _ => new SpyStorageProvider("private"));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<SplitStorageProvider>(provider.GetRequiredService<IStorageProvider>());
    }

    [Fact]
    public void The_factory_form_throws_when_a_storage_provider_is_already_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStorageProvider>(new SpyStorageProvider("single"));

        Assert.Throws<InvalidOperationException>(() => services.AddThemiaSplitStorage(
            _ => new SpyStorageProvider("public", hasPublicContainer: true), _ => new SpyStorageProvider("private")));
    }

    [Fact]
    public void The_container_disposes_the_router_and_through_it_both_slots()
    {
        var publicSlot = new SpyStorageProvider("public", hasPublicContainer: true);
        var privateSlot = new SpyStorageProvider("private");
        var services = new ServiceCollection();
        services.AddThemiaSplitStorage(publicSlot, privateSlot);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IStorageProvider>();

        provider.Dispose();

        Assert.Equal(1, publicSlot.DisposeCount);
        Assert.Equal(1, privateSlot.DisposeCount);
    }

    [Fact]
    public async Task IStorageUrlService_joins_a_local_link_onto_the_base_and_leaves_a_public_slot_url_absolute()
    {
        var local = new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = root,
            SigningKey = "k-long-enough-for-an-hmac-key-0123",
        });
        var router = new SplitStorageProvider(new SpyStorageProvider("cdn", hasPublicContainer: true), local);
        var urls = new StorageUrlService(
            router, Options.Create(new StorageUrlOptions { PresignedBaseUrl = "https://api.example.com/api/v1/storage" }));

        var privateUrl = await urls.GetDownloadUrlAsync("verifications/1/a.pdf", TimeSpan.FromMinutes(5));
        var publicUrl = await urls.GetDownloadUrlAsync("public/listings/1/a.jpg", TimeSpan.FromMinutes(5));

        Assert.StartsWith("https://api.example.com/api/v1/storage/_local/get?", privateUrl.AbsoluteUri);
        Assert.Equal("https://cdn.example.com/public/listings/1/a.jpg?sig=1", publicUrl.AbsoluteUri);
    }
}
