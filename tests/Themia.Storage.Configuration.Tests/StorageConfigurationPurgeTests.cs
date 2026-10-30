using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Configuration.Tests;

/// <summary>The binding wraps the public S3 slot with the CDN purge when a purger is registered, and registers the
/// <see cref="LocalUrlSigner"/> that <c>MapThemiaLocalStorage</c> verifies with.</summary>
public sealed class StorageConfigurationPurgeTests : IDisposable
{
    private const string SigningKey = "sentinel-signing-key-0123456789-abcdef";

    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "themia-storage-purge", Guid.NewGuid().ToString("N"));
    private readonly S3Stub stub = new();

    public StorageConfigurationPurgeTests() => Directory.CreateDirectory(contentRoot);

    public void Dispose()
    {
        stub.Dispose();
        Directory.Delete(contentRoot, recursive: true);
    }

    private Dictionary<string, string?> S3Settings() => new()
    {
        ["Storage:Local:RootPath"] = "private",
        ["Storage:Local:SigningKey"] = SigningKey,
        ["Storage:Public:Provider"] = "R2",
        ["Storage:S3:PublicBucketName"] = "photos",
        ["Storage:S3:PublicBaseUrl"] = "https://img.example.com",
        ["Storage:S3:ServiceUrl"] = stub.Url,
        ["Storage:S3:ForcePathStyle"] = "true",
        ["Storage:S3:AccessKey"] = "dummy-access",
        ["Storage:S3:SecretKey"] = "dummy-secret",
    };

    private async Task<IHost> Start(Dictionary<string, string?> settings, Action<IServiceCollection>? before = null)
    {
        var host = new HostBuilder()
            .UseContentRoot(contentRoot)
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) =>
            {
                before?.Invoke(services);
                services.AddThemiaStorageFromConfiguration(context.Configuration);
            })
            .Build();
        await host.StartAsync();
        return host;
    }

    [Fact]
    public async Task Deleting_a_public_key_purges_its_public_url_when_a_purger_is_registered()
    {
        var purger = new RecordingPurger();
        using var host = await Start(S3Settings(), services => services.AddSingleton<ICdnPurger>(purger));

        var provider = host.Services.GetRequiredService<IStorageProvider>();
        await provider.DeleteAsync("public/x.jpg");

        Assert.Contains(stub.Requests, r => r.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.Equal(provider.GetPublicUrl("public/x.jpg"), Assert.Single(purger.Urls));
    }

    [Fact]
    public async Task Deleting_a_private_key_never_reaches_the_stub_or_the_purger()
    {
        var purger = new RecordingPurger();
        using var host = await Start(S3Settings(), services => services.AddSingleton<ICdnPurger>(purger));

        await host.Services.GetRequiredService<IStorageProvider>().DeleteAsync("verifications/id.pdf");

        Assert.Empty(stub.Requests);
        Assert.Empty(purger.Urls);
    }

    [Fact]
    public async Task Without_a_purger_a_public_delete_still_reaches_the_bucket()
    {
        using var host = await Start(S3Settings());

        await host.Services.GetRequiredService<IStorageProvider>().DeleteAsync("public/x.jpg");

        Assert.Contains(stub.Requests, r => r.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_registered_signer_verifies_the_tokens_the_provider_mints()
    {
        using var host = await Start(S3Settings());

        var provider = host.Services.GetRequiredService<IStorageProvider>();
        var signer = host.Services.GetRequiredService<LocalUrlSigner>();
        var url = await provider.GetPresignedUrlAsync("t1/a.txt", new PresignedUrlRequest(PresignedUrlOperation.Get, TimeSpan.FromMinutes(5)));
        var token = Uri.UnescapeDataString(url.OriginalString.Split("token=")[1]);

        Assert.True(signer.TryVerify("t1/a.txt", PresignedUrlOperation.Get, token, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task An_app_registered_signer_is_kept()
    {
        var own = new LocalUrlSigner("another-signing-key-0123456789-abcdef");
        using var host = await Start(S3Settings(), services => services.AddSingleton(own));

        Assert.Same(own, host.Services.GetRequiredService<LocalUrlSigner>());
    }

    private sealed class RecordingPurger : ICdnPurger
    {
        public List<Uri> Urls { get; } = [];

        public Task PurgeAsync(Uri url, CancellationToken cancellationToken = default)
        {
            Urls.Add(url);
            return Task.CompletedTask;
        }
    }

    /// <summary>A loopback endpoint that answers every S3 request with 204 and records "METHOD path".</summary>
    private sealed class S3Stub : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly List<string> requests = [];

        public S3Stub()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Url + "/");
            listener.Start();
            _ = Task.Run(Serve);
        }

        public string Url { get; }

        public IReadOnlyList<string> Requests
        {
            get
            {
                lock (requests)
                {
                    return [.. requests];
                }
            }
        }

        public void Dispose() => listener.Close();

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                lock (requests)
                {
                    requests.Add($"{context.Request.HttpMethod} {context.Request.Url?.AbsolutePath}");
                }

                context.Response.StatusCode = 204;
                context.Response.Close();
            }
        }
    }
}
