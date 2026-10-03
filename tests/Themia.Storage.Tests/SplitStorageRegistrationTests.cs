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
    public void The_instance_form_leaves_disposing_the_slots_to_the_app_that_built_them()
    {
        // As with any instance given to the container: the app built the slots, so the app disposes them.
        var publicSlot = new SpyStorageProvider("public", hasPublicContainer: true);
        var privateSlot = new SpyStorageProvider("private");
        var services = new ServiceCollection();
        services.AddThemiaSplitStorage(publicSlot, privateSlot);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IStorageProvider>();

        provider.Dispose();

        Assert.Equal(0, publicSlot.DisposeCount);
        Assert.Equal(0, privateSlot.DisposeCount);
    }

    [Fact]
    public async Task A_temporary_container_built_from_the_same_collection_does_not_break_the_next_one()
    {
        // BuildServiceProvider() during start-up, resolved and disposed, must not take the slots with it.
        var services = new ServiceCollection();
        services.AddThemiaSplitStorage(new SpyStorageProvider("public", hasPublicContainer: true), new SpyStorageProvider("private"));
        using (var temporary = services.BuildServiceProvider())
        {
            _ = temporary.GetRequiredService<IStorageProvider>();
        }

        using var real = services.BuildServiceProvider();

        Assert.False(await real.GetRequiredService<IStorageProvider>().ExistsAsync("public/a.jpg"));
    }

    [Fact]
    public void The_factory_form_does_not_dispose_slots_the_container_already_owns()
    {
        // Slots resolved from the container are disposed by the container, once. A router that also disposed
        // them would dispose each twice at shutdown.
        var services = new ServiceCollection();
        services.AddSingleton(new SpyStorageProvider("public", hasPublicContainer: true));
        services.AddSingleton(new SpyStorageProvider("private"));
        services.AddThemiaSplitStorage(
            sp => sp.GetServices<SpyStorageProvider>().First(),
            sp => sp.GetServices<SpyStorageProvider>().Last());
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IStorageProvider>();

        provider.Dispose();

        Assert.All(services.Select(d => d.ImplementationInstance).OfType<SpyStorageProvider>(), spy => Assert.Equal(0, spy.DisposeCount));
    }

    [Fact]
    public void A_keyed_provider_registration_does_not_count_as_an_already_registered_provider()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IStorageProvider>("public", new SpyStorageProvider("public", hasPublicContainer: true));
        services.AddKeyedSingleton<IStorageProvider>("private", new SpyStorageProvider("private"));

        services.AddThemiaSplitStorage(
            sp => sp.GetRequiredKeyedService<IStorageProvider>("public"),
            sp => sp.GetRequiredKeyedService<IStorageProvider>("private"));

        using var provider = services.BuildServiceProvider();
        Assert.IsType<SplitStorageProvider>(provider.GetRequiredService<IStorageProvider>());
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
