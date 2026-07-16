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

/// <summary>The public Local route must serve unauthenticated clients even when the returned group is
/// gated with <c>RequireAuthorization()</c> — it lives in the ungated <c>transfer</c> group on purpose,
/// because a public URL that 401s in an &lt;img&gt; tag looks right and fails at render time. It must
/// also stay unreachable for private objects and reject traversal, and the gated broker routes must stay
/// gated.</summary>
public sealed class PublicRouteTests : IAsyncLifetime
{
    private const string SigningKey = "test-signing-key-at-least-32-characters-long";
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-public", Guid.NewGuid().ToString("N"));
    private readonly string publicRoot = Path.Combine(Path.GetTempPath(), "themia-storage-public-pub", Guid.NewGuid().ToString("N"));

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
                o.PublicRootPath = publicRoot;
                o.PublicBaseUrl = "http://127.0.0.1/storage/public";
                o.SigningKey = SigningKey;
            });

        builder.Services.AddSingleton<ITenantContext>(new TenantContext(new TenantId("acme")));
        builder.Services.AddSingleton<ICurrentUserAccessor>(new StubUser());

        // A scheme that never authenticates: every request is anonymous, so anything the adopter gated
        // with RequireAuthorization() answers 401. This is the browser fetching a public/presigned URL.
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
        if (Directory.Exists(publicRoot)) Directory.Delete(publicRoot, recursive: true);
    }

    [Fact]
    public async Task Public_route_serves_an_unauthenticated_client_when_the_group_requires_auth()
    {
        await provider.PutAsync("public/t1/hero.jpg", new MemoryStream(Encoding.UTF8.GetBytes("image-bytes")),
            new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

        var response = await client.GetAsync("/storage/public/t1/hero.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image-bytes", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Public_route_sets_a_cacheable_cache_control()
    {
        await provider.PutAsync("public/t1/hero.jpg", new MemoryStream([1]), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));

        var response = await client.GetAsync("/storage/public/t1/hero.jpg");

        // The deliberate OPPOSITE of the dashboards' no-store: these bytes are not sensitive, and defeating
        // the CDN is the whole failure mode this feature exists to avoid.
        Assert.True(response.Headers.CacheControl!.Public);
    }

    [Fact]
    public async Task Public_route_sets_anti_xss_headers()
    {
        // A public object with an executable Content-Type (text/html, image/svg+xml) served same-origin
        // (Local) is stored XSS unless active content is neutralized at serve time. These headers do that
        // without affecting images/video/audio.
        await provider.PutAsync("public/t1/payload.html", new MemoryStream(Encoding.UTF8.GetBytes("<script>alert(1)</script>")),
            new StoragePutOptions("text/html", Visibility: StorageVisibility.Public));

        var response = await client.GetAsync("/storage/public/t1/payload.html");

        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var nosniff));
        Assert.Equal("nosniff", Assert.Single(nosniff));
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var csp));
        Assert.Equal("sandbox; default-src 'none'", Assert.Single(csp));
    }

    [Fact]
    public async Task Public_route_cannot_reach_a_private_object()
    {
        // Same tail, private container. The public route must not serve it under any key shape.
        await provider.PutAsync("t1/secret.pdf", new MemoryStream(Encoding.UTF8.GetBytes("secret")), new StoragePutOptions("application/pdf"));

        var response = await client.GetAsync("/storage/public/t1/secret.pdf");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Public_route_rejects_traversal()
    {
        var response = await client.GetAsync("/storage/public/..%2F..%2Fblobs%2Ft1%2Fsecret.pdf");

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Broker_routes_stay_gated()
    {
        var response = await client.GetAsync("/storage/t1/anything.jpg");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

/// <summary>The startup guard in <c>MapThemiaStorageEndpoints</c> is the exact mechanism that stops every
/// public URL from silently 404ing in production: a Local <c>PublicBaseUrl</c> that is absolute (so it
/// passes <c>UseLocal</c>'s own validation) but does not end with the route mount <c>{prefix}/public</c>
/// must fail fast at mapping time, not at render time.</summary>
public sealed class PublicMountPathGuardTests
{
    private const string SigningKey = "test-signing-key-at-least-32-characters-long";

    [Fact]
    public void Mapping_throws_when_PublicBaseUrl_does_not_end_with_the_route_mount()
    {
        var app = BuildApp("http://127.0.0.1/wrong-path");
        try
        {
            // Absolute http, so UseLocal's own Validate() already passed; the only remaining thrower is the
            // mount-path guard. Assert on its message so a future UseLocal change can't mask this test.
            var ex = Assert.Throws<InvalidOperationException>(() => app.MapThemiaStorageEndpoints());
            Assert.Contains("must end with the public route mount", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            ((IDisposable)app).Dispose();
        }
    }

    [Fact]
    public void Mapping_does_not_throw_when_PublicBaseUrl_ends_with_the_route_mount()
    {
        var app = BuildApp("http://127.0.0.1/storage/public");
        try
        {
            app.MapThemiaStorageEndpoints(); // does not throw
        }
        finally
        {
            ((IDisposable)app).Dispose();
        }
    }

    private static WebApplication BuildApp(string publicBaseUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "themia-storage-guard", Guid.NewGuid().ToString("N"));
        var publicRoot = Path.Combine(Path.GetTempPath(), "themia-storage-guard-pub", Guid.NewGuid().ToString("N"));

        var builder = WebApplication.CreateBuilder();
        builder.Services
            .AddThemiaStorage(o => o.DefaultTenantQuotaBytes = 1024L * 1024 * 1024)
            .UseLocal(o =>
            {
                o.RootPath = root;
                o.PublicRootPath = publicRoot;
                o.PublicBaseUrl = publicBaseUrl;
                o.SigningKey = SigningKey;
            });

        return builder.Build();
    }
}
