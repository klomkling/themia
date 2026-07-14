using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Storage.DependencyInjection;
using Themia.Modules.Storage.Endpoints;
using Themia.Storage;
using Xunit;

namespace Themia.Modules.Storage.AspNetCore.IntegrationTests;

/// <summary>The presigned Local transfer routes must survive the adopter gating the returned group with
/// <c>RequireAuthorization()</c> — the documented way to protect the broker endpoints. A presigned URL is
/// self-authorizing (the HMAC token IS the credential, exactly like an S3 presigned URL), so it must work
/// for an unauthenticated client; otherwise Local silently behaves differently from S3/R2 and a signed URL
/// in an &lt;img&gt; tag 401s.</summary>
public sealed class AuthorizedGroupRouteTests : IAsyncLifetime
{
    private const string SigningKey = "test-signing-key-at-least-32-characters-long";
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-auth", Guid.NewGuid().ToString("N"));

    private WebApplication app = null!;
    private HttpClient client = null!;
    private IStorageProvider provider = null!;

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

        // A scheme that never authenticates: every request is anonymous, so anything the adopter gated
        // with RequireAuthorization() answers 401. This is the browser fetching a presigned URL.
        builder.Services.AddAuthentication(AnonymousScheme)
            .AddScheme<AuthenticationSchemeOptions, AlwaysAnonymousHandler>(AnonymousScheme, _ => { });
        builder.Services.AddAuthorization();

        app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // The documented adopter usage (see MapThemiaStorageEndpoints' <returns>).
        app.MapThemiaStorageEndpoints().RequireAuthorization();
        await app.StartAsync();

        provider = app.Services.GetRequiredService<IStorageProvider>();
        client = new HttpClient { BaseAddress = new Uri(BaseAddress()) };
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();
        await app.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Signed_local_get_serves_an_unauthenticated_client_when_the_group_requires_auth()
    {
        const string key = "acme/docs/public-ish.txt";
        var payload = Encoding.UTF8.GetBytes("hello themia");
        using var content = new MemoryStream(payload);
        await provider.PutAsync(key, content, new StoragePutOptions("text/plain"));

        var response = await client.GetAsync(await SignedUrlAsync(key, PresignedUrlOperation.Get));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Signed_local_put_accepts_an_unauthenticated_client_when_the_group_requires_auth()
    {
        const string key = "acme/docs/uploaded.txt";
        using var body = new ByteArrayContent(Encoding.UTF8.GetBytes("uploaded"));
        body.Headers.Add("Content-Type", "text/plain");

        var response = await client.PutAsync(await SignedUrlAsync(key, PresignedUrlOperation.Put), body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Broker_routes_stay_gated_when_the_group_requires_auth()
    {
        // The other half of the contract: gating the group must still protect the broker endpoints.
        // Without this, a fix that ungates the presigned routes could ungate everything.
        var response = await client.GetAsync("/storage/acme/docs/anything.txt");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> SignedUrlAsync(string key, PresignedUrlOperation operation)
    {
        var relative = await provider.GetPresignedUrlAsync(key, new PresignedUrlRequest(operation, TimeSpan.FromMinutes(5)));
        return $"/storage/{relative}";
    }

    private string BaseAddress()
    {
        var feature = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        return feature.Addresses.First();
    }

    private const string AnonymousScheme = "Anonymous";

    private sealed class AlwaysAnonymousHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public AlwaysAnonymousHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class StubUser : ICurrentUserAccessor
    {
        public string? UserId => "tester";
    }
}
