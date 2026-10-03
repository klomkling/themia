using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Storage.Local;
using Themia.Storage.Urls;
using Xunit;

namespace Themia.Storage.AspNetCore.Tests;

/// <summary>
/// The router in a real host (coord #0152): a Local presigned link minted over it still opens, and the factory
/// form refuses to start with a half-configured slot instead of failing at the first request.
/// </summary>
public sealed class SplitStorageHostTests : IDisposable
{
    private const string SigningKey = "test-signing-key-that-is-long-enough-for-hmac";
    private const string Mount = "/api/v1/storage";

    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-split-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private LocalStorageProvider NewLocal() => new(new LocalStorageOptions { RootPath = root, SigningKey = SigningKey });

    private static Task<IHost> StartAsync(Action<IServiceCollection> services, bool mountLocalRoute = true) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton(new LocalUrlSigner(SigningKey));
                    services(s);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e =>
                    {
                        if (mountLocalRoute)
                        {
                            e.MapThemiaLocalStorage(Mount);
                        }
                    });
                }))
            .StartAsync();

    private async Task<(IHost Host, Uri Link)> StartWithAPrivateObjectAsync(bool mountLocalRoute)
    {
        var host = await StartAsync(
            s =>
            {
                s.AddThemiaSplitStorage(new CdnPublicSlot(), NewLocal());
                s.AddThemiaStorageUrls(o => o.PresignedBaseUrl = "http://localhost" + Mount);
            },
            mountLocalRoute);
        var storage = host.Services.GetRequiredService<IStorageProvider>();
        await storage.PutAsync("verifications/1/id.pdf", new MemoryStream([1, 2, 3]), new StoragePutOptions("application/pdf"));
        var link = await host.Services.GetRequiredService<IStorageUrlService>()
            .GetDownloadUrlAsync("verifications/1/id.pdf", TimeSpan.FromMinutes(5));
        return (host, link);
    }

    [Fact]
    public async Task A_local_presigned_link_minted_over_the_router_opens_and_returns_the_bytes()
    {
        var (host, link) = await StartWithAPrivateObjectAsync(mountLocalRoute: true);
        using var _ = host;

        var response = await host.GetTestClient().GetAsync(link.PathAndQuery);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1, 2, 3], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Without_the_mount_the_same_link_answers_404_which_is_what_the_round_trip_test_guards()
    {
        var (host, link) = await StartWithAPrivateObjectAsync(mountLocalRoute: false);
        using var _ = host;

        var response = await host.GetTestClient().GetAsync(link.PathAndQuery);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_factory_form_fails_host_start_when_a_slot_cannot_be_built()
    {
        // The local options are validated inside the factory, as an app using the options pipeline would.
        var blankSigningKey = new LocalStorageOptions { RootPath = root };

        await Assert.ThrowsAnyAsync<Exception>(() => StartAsync(s => s.AddThemiaSplitStorage(
            _ => new CdnPublicSlot(),
            _ =>
            {
                blankSigningKey.Validate();
                return new LocalStorageProvider(blankSigningKey);
            })));
    }

    [Fact]
    public async Task A_plain_lazy_registration_of_the_same_bad_slot_starts_fine_which_is_the_gap_the_factory_form_closes()
    {
        var blankSigningKey = new LocalStorageOptions { RootPath = root };

        using var host = await StartAsync(s => s.AddSingleton<IStorageProvider>(_ =>
        {
            blankSigningKey.Validate();
            return new LocalStorageProvider(blankSigningKey);
        }));

        Assert.Throws<ArgumentException>(() => host.Services.GetRequiredService<IStorageProvider>());
    }

    /// <summary>A public slot that only answers GetPublicUrl, for tests that exercise the private side.</summary>
    private sealed class CdnPublicSlot : IStorageProvider
    {
        public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Uri GetPublicUrl(string key) => new($"https://cdn.example.com/{StorageKey.StripVisibilityPrefix(key)}");
    }
}
