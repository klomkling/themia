using Microsoft.Extensions.DependencyInjection;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// The decorator that purges the CDN edge after a public delete (coord #0153): delete first, purge only
/// public keys, let a refused purge surface, and compose with the split router without owning its slot.
/// </summary>
public sealed class PurgingStorageProviderTests
{
    private readonly SpyStorageProvider inner = new("cdn", hasPublicContainer: true);
    private readonly SpyCdnPurger purger;

    public PurgingStorageProviderTests() => purger = new SpyCdnPurger(inner.Calls);

    private PurgingStorageProvider Create() => new(inner, purger);

    [Fact]
    public async Task A_public_delete_removes_the_object_and_only_then_purges_the_url_the_slot_serves()
    {
        await Create().DeleteAsync("public/a.jpg");

        Assert.Equal(new Uri("https://cdn.example.com/a.jpg"), Assert.Single(purger.Purged));
        Assert.Contains("Delete:public/a.jpg", purger.InnerCallsAtPurge);
    }

    [Fact]
    public async Task A_failed_delete_throws_the_inner_exception_and_never_purges()
    {
        var failing = new SpyStorageProvider("cdn", hasPublicContainer: true) { ThrowOnDelete = new IOException("disk gone") };
        var provider = new PurgingStorageProvider(failing, purger);

        await Assert.ThrowsAsync<IOException>(() => provider.DeleteAsync("public/a.jpg"));

        Assert.Empty(purger.Purged);
    }

    [Fact]
    public async Task A_public_key_whose_url_cannot_be_built_fails_before_anything_is_deleted()
    {
        var noContainer = new SpyStorageProvider("cdn", hasPublicContainer: false);
        var provider = new PurgingStorageProvider(noContainer, purger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DeleteAsync("public/a.jpg"));

        Assert.DoesNotContain("Delete:public/a.jpg", noContainer.Calls);
    }

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("private/a.pdf")]
    [InlineData("Public/a.jpg")]
    [InlineData("_platform/x")]
    public async Task A_key_outside_public_is_deleted_and_never_purged(string key)
    {
        await Create().DeleteAsync(key);

        Assert.Contains($"Delete:{key}", inner.Calls);
        Assert.Empty(purger.Purged);
    }

    [Fact]
    public async Task A_backslash_key_is_normalised_before_it_is_classified()
    {
        await Create().DeleteAsync(@"public\a.jpg");

        Assert.Contains("Delete:public/a.jpg", inner.Calls);
        Assert.Single(purger.Purged);
    }

    [Theory]
    [InlineData("/public/a.jpg")]
    [InlineData("public/../a.jpg")]
    public async Task An_invalid_key_throws_and_nothing_is_touched(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Create().DeleteAsync(key));

        Assert.Empty(inner.Calls);
        Assert.Empty(purger.Purged);
    }

    [Fact]
    public async Task A_refused_purge_surfaces_after_the_object_is_already_deleted()
    {
        purger.Throws = new CdnPurgeException(new Uri("https://cdn.example.com/a.jpg"), "refused", httpStatus: 429);

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => Create().DeleteAsync("public/a.jpg"));

        Assert.Equal(429, thrown.HttpStatus);
        Assert.Contains("Delete:public/a.jpg", inner.Calls);
    }

    [Fact]
    public async Task Any_other_purger_failure_after_the_delete_is_reported_as_CdnPurgeException()
    {
        var boom = new InvalidOperationException("boom");
        purger.Throws = boom;

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => Create().DeleteAsync("public/a.jpg"));

        Assert.Same(boom, thrown.InnerException);
        Assert.Equal(new Uri("https://cdn.example.com/a.jpg"), thrown.Url);
        Assert.Null(thrown.HttpStatus);
        Assert.Contains("Delete:public/a.jpg", inner.Calls);
        Assert.DoesNotContain("boom", thrown.Message);
    }

    [Fact]
    public async Task A_CdnPurgeException_from_the_purger_passes_through_as_the_same_instance()
    {
        var refused = new CdnPurgeException(new Uri("https://cdn.example.com/a.jpg"), "refused", httpStatus: 429);
        purger.Throws = refused;

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => Create().DeleteAsync("public/a.jpg"));

        Assert.Same(refused, thrown);
    }

    [Fact]
    public async Task The_callers_own_cancellation_during_the_purge_stays_an_OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        purger.Throws = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create().DeleteAsync("public/a.jpg", cts.Token));
    }

    [Fact]
    public async Task Every_other_member_forwards_and_never_purges()
    {
        var provider = Create();
        var presign = new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(1));

        await provider.PutAsync("public/a.jpg", new MemoryStream([1]), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));
        await provider.GetAsync("public/a.jpg");
        await provider.ExistsAsync("public/a.jpg");
        await provider.StatAsync("public/a.jpg");
        await provider.GetPresignedUrlAsync("public/a.jpg", presign);
        var url = provider.GetPublicUrl("public/a.jpg");

        Assert.Equal(new Uri("https://cdn.example.com/a.jpg"), url);
        Assert.Equal(
            new[] { "Put:public/a.jpg", "Get:public/a.jpg", "Exists:public/a.jpg", "Stat:public/a.jpg", "Presign:public/a.jpg", "PublicUrl:public/a.jpg" },
            inner.Calls);
        Assert.Empty(purger.Purged);
    }

    [Fact]
    public async Task Wrapping_the_public_slot_of_the_router_purges_public_deletes_only()
    {
        var privateSlot = new SpyStorageProvider("private");
        var router = new SplitStorageProvider(new PurgingStorageProvider(inner, purger), privateSlot);

        await router.DeleteAsync("public/a.jpg");
        await router.DeleteAsync("docs/id.pdf");

        Assert.Single(purger.Purged);
        Assert.Contains("Delete:docs/id.pdf", privateSlot.Calls);
    }

    [Fact]
    public void The_container_disposes_a_wrapped_slot_exactly_once()
    {
        var slot = new SpyStorageProvider("cdn", hasPublicContainer: true);
        var services = new ServiceCollection();
        services.AddSingleton<ICdnPurger>(purger);
        services.AddSingleton(_ => slot);
        services.AddThemiaSplitStorage(
            sp => new PurgingStorageProvider(sp.GetRequiredService<SpyStorageProvider>(), sp.GetRequiredService<ICdnPurger>()),
            _ => new SpyStorageProvider("private"));

        using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<IStorageProvider>();
        }

        Assert.Equal(1, slot.DisposeCount);
    }

    [Fact]
    public void A_decorator_the_container_owns_never_disposes_the_slot_it_wraps()
    {
        var slot = new SpyStorageProvider("cdn", hasPublicContainer: true);
        var services = new ServiceCollection();
        services.AddSingleton(_ => slot);
        services.AddSingleton(sp => new PurgingStorageProvider(sp.GetRequiredService<SpyStorageProvider>(), purger));

        using (var provider = services.BuildServiceProvider())
        {
            _ = provider.GetRequiredService<PurgingStorageProvider>();
        }

        Assert.Equal(1, slot.DisposeCount);
    }

    [Fact]
    public async Task A_refused_purge_leaves_the_object_deleted_and_a_second_delete_completes_the_pair()
    {
        var root = Path.Combine(Path.GetTempPath(), "themia-purge-private", Guid.NewGuid().ToString("N"));
        var publicRoot = Path.Combine(Path.GetTempPath(), "themia-purge-public", Guid.NewGuid().ToString("N"));
        try
        {
            var local = new LocalStorageProvider(new LocalStorageOptions
            {
                RootPath = root,
                PublicRootPath = publicRoot,
                PublicBaseUrl = "https://cdn.example.com/media",
                SigningKey = "test-signing-key-at-least-32-characters-long",
            });
            await local.PutAsync("public/a.jpg", new MemoryStream([1]), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));
            var flaky = new SpyCdnPurger([]) { Throws = new CdnPurgeException(new Uri("https://cdn.example.com/media/a.jpg"), "refused", 429) };
            var provider = new PurgingStorageProvider(local, flaky);

            await Assert.ThrowsAsync<CdnPurgeException>(() => provider.DeleteAsync("public/a.jpg"));
            Assert.False(await local.ExistsAsync("public/a.jpg"));

            flaky.Throws = null;
            await provider.DeleteAsync("public/a.jpg");

            Assert.Equal(2, flaky.Purged.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(publicRoot)) Directory.Delete(publicRoot, recursive: true);
        }
    }
}

/// <summary>A purger that records what it was asked to purge and what the inner provider had seen by then.</summary>
internal sealed class SpyCdnPurger(List<string> innerCalls) : ICdnPurger
{
    public List<Uri> Purged { get; } = [];

    public List<string> InnerCallsAtPurge { get; } = [];

    public Exception? Throws { get; set; }

    public Task PurgeAsync(Uri url, CancellationToken cancellationToken = default)
    {
        InnerCallsAtPurge.AddRange(innerCalls);
        Purged.Add(url);
        return Throws is null ? Task.CompletedTask : Task.FromException(Throws);
    }
}
