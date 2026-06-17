using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Storage.DependencyInjection;
using Themia.Modules.Storage.Endpoints;
using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Modules.Storage.AspNetCore.IntegrationTests;

/// <summary>End-to-end HTTP tests for the Local signed download/upload routes. A signed presigned URL
/// must actually serve/accept bytes over real HTTP, and tampered/expired tokens must be rejected.</summary>
public sealed class LocalSignedRouteTests : IAsyncLifetime
{
    private const string SigningKey = "test-signing-key-at-least-32-characters-long";
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-http", Guid.NewGuid().ToString("N"));

    private WebApplication app = null!;
    private HttpClient client = null!;
    private IStorageProvider provider = null!;
    private LocalUrlSigner signer = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services
            .AddThemiaStorage(o => o.DefaultTenantQuotaBytes = 1024L * 1024 * 1024)
            .UseLocal(o =>
            {
                o.RootPath = root;
                o.SigningKey = SigningKey;
            });

        builder.Services.AddSingleton<ITenantContext>(new TenantContext(new TenantId("acme")));
        builder.Services.AddSingleton<ICurrentUserAccessor>(new StubUser());

        app = builder.Build();
        app.MapThemiaStorageEndpoints();
        await app.StartAsync();

        provider = app.Services.GetRequiredService<IStorageProvider>();
        signer = app.Services.GetRequiredService<LocalUrlSigner>();
        client = new HttpClient { BaseAddress = new Uri(BaseAddress()) };
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        await app.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Signed_put_then_get_round_trips_bytes_over_http()
    {
        const string key = "acme/docs/hello.txt";
        var payload = Encoding.UTF8.GetBytes("hello themia");

        // Build a signed PUT URL via the provider, rebase to the test server, and upload.
        var putUrl = await AbsoluteAsync(key, PresignedUrlOperation.Put);
        using var putContent = new ByteArrayContent(payload);
        putContent.Headers.Add("Content-Type", "text/plain");
        var putResponse = await client.PutAsync(putUrl, putContent);
        Assert.Equal(HttpStatusCode.NoContent, putResponse.StatusCode);

        // Build a signed GET URL and download.
        var getUrl = await AbsoluteAsync(key, PresignedUrlOperation.Get);
        var getResponse = await client.GetAsync(getUrl);
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var downloaded = await getResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(payload, downloaded);
    }

    [Fact]
    public async Task Tampered_token_is_rejected_with_401()
    {
        const string key = "acme/docs/secret.txt";
        var url = await AbsoluteAsync(key, PresignedUrlOperation.Get);
        // Flip the last character of the token to invalidate the signature.
        var tampered = url[..^1] + (url[^1] == 'A' ? 'B' : 'A');

        var response = await client.GetAsync(tampered);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected_with_401()
    {
        const string key = "acme/docs/old.txt";
        // Sign with an already-elapsed expiry so the route rejects it.
        var token = signer.Sign(key, "get", DateTimeOffset.UtcNow.AddMinutes(-1));
        var url = $"/storage/_local/get?key={Uri.EscapeDataString(key)}&token={Uri.EscapeDataString(token)}";

        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> AbsoluteAsync(string key, PresignedUrlOperation operation)
    {
        var relative = await provider.GetPresignedUrlAsync(key, new PresignedUrlRequest(operation, TimeSpan.FromMinutes(5)));
        Assert.False(relative.IsAbsoluteUri);
        return $"/storage/{relative}";
    }

    private string BaseAddress()
    {
        var feature = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        return feature!.Addresses.First();
    }

    private sealed class StubUser : ICurrentUserAccessor
    {
        public string? UserId => "tester";
    }
}
