using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.AspNetCore.Tests;

/// <summary>
/// The Local public container route (coord #0154): a host serves the public container at the path of
/// <c>PublicBaseUrl</c> without having to know where the provider writes, and only the public container.
/// </summary>
public sealed class LocalPublicStorageEndpointsTests : IAsyncLifetime
{
    private const string PublicPath = "/media";

    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-public-" + Guid.NewGuid().ToString("N"));
    private LocalStorageProvider provider = null!;
    private IHost host = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        provider = CreateProvider();
        host = await StartAsync(provider);
        client = host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        client.Dispose();
        await host.StopAsync();
        host.Dispose();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private LocalStorageProvider CreateProvider(string? publicBaseUrl = "https://cdn.example.com" + PublicPath) =>
        new(new LocalStorageOptions
        {
            RootPath = Path.Combine(root, "private"),
            PublicRootPath = publicBaseUrl is null ? string.Empty : Path.Combine(root, "public"),
            PublicBaseUrl = publicBaseUrl ?? string.Empty,
        });

    // A provider over the SAME roots as `provider`, with a different public base url.
    private LocalStorageProvider CreateProviderSharing(string publicBaseUrl) =>
        new(new LocalStorageOptions
        {
            RootPath = Path.Combine(root, "private"),
            PublicRootPath = Path.Combine(root, "public"),
            PublicBaseUrl = publicBaseUrl,
        });

    private static Task<IHost> StartAsync(
        LocalStorageProvider local,
        Action<IServiceCollection>? services = null,
        Action<IApplicationBuilder>? pipeline = null,
        string? mount = null) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    services?.Invoke(s);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    pipeline?.Invoke(app);
                    app.UseEndpoints(e => e.MapThemiaLocalPublicStorage(local, mount));
                }))
            .StartAsync();

    private Task PutAsync(string key, string body, string contentType) =>
        provider.PutAsync(
            key,
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)),
            new StoragePutOptions(contentType, Visibility: StorageKey.IsPublic(key) ? StorageVisibility.Public : StorageVisibility.Private));

    [Fact]
    public async Task A_public_object_is_served_at_its_public_url_path_with_its_type()
    {
        await PutAsync("public/photos/a.txt", "hello", "text/plain");
        var path = provider.GetPublicUrl("public/photos/a.txt").AbsolutePath;

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_key_that_needs_url_encoding_is_served()
    {
        await PutAsync("public/photos/a b.txt", "spaced", "text/plain");
        var path = provider.GetPublicUrl("public/photos/a b.txt").AbsolutePath;

        var response = await client.GetAsync(path);

        Assert.Equal("spaced", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_public_object_is_served_with_nosniff_and_a_sandbox_but_is_not_marked_uncacheable()
    {
        // The sandbox is what stops an uploaded SVG running script on this origin. Cache-Control is left to the
        // host (and the CDN in front): a public object is meant to be cached, unlike a presigned download.
        await PutAsync("public/x.svg", "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>", "image/svg+xml");

        var response = await client.GetAsync($"{PublicPath}/x.svg");

        Assert.Equal("sandbox; default-src 'none'", string.Join(",", response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Null(response.Headers.CacheControl);
    }

    [Fact]
    public async Task A_missing_object_is_404()
    {
        var response = await client.GetAsync($"{PublicPath}/never-written.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_private_object_is_never_reachable_through_the_public_route()
    {
        await PutAsync("docs/secret.txt", "private", "text/plain");

        var response = await client.GetAsync($"{PublicPath}/docs/secret.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/media/../../private/blobs/docs/secret.txt")]
    [InlineData("/media/photos/../../../private/blobs/docs/secret.txt")]
    public async Task A_traversal_key_is_404_and_never_reads_the_private_blob_it_names(string path)
    {
        // HttpClient collapses ".." before sending, so the request is built on the server's HttpContext instead and
        // the key reaches the handler as written. The secret really exists at the path the key walks to.
        await PutAsync("docs/secret.txt", "private", "text/plain");
        Assert.True(File.Exists(Path.Combine(root, "private", "blobs", "docs", "secret.txt")));

        var context = await host.GetTestServer().SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
        });

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        using var body = new StreamReader(context.Response.Body);
        Assert.DoesNotContain("private", await body.ReadToEndAsync());
    }

    [Fact]
    public async Task A_host_requiring_login_everywhere_still_serves_a_public_object()
    {
        await PutAsync("public/a.txt", "hello", "text/plain");
        using var hardened = await StartAsync(
            provider,
            services: s =>
            {
                s.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, NoUserHandler>("Test", _ => { });
                s.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
            },
            pipeline: app =>
            {
                app.UseAuthentication();
                app.UseAuthorization();
            });

        var response = await hardened.GetTestClient().GetAsync($"{PublicPath}/a.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Mapping_a_provider_with_no_public_container_fails_at_startup()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(CreateProvider(publicBaseUrl: null)));

        Assert.Contains("public container", error.Message);
    }

    // ---- the mount -------------------------------------------------------------------------------

    [Fact]
    public async Task A_public_base_url_with_no_path_needs_an_explicit_mount_rather_than_a_root_catch_all()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(CreateProvider("https://cdn.example.com")));

        Assert.Contains("mount", error.Message);
    }

    [Fact]
    public async Task An_explicit_mount_serves_a_public_base_url_that_has_no_path()
    {
        var local = CreateProvider("https://cdn.example.com");
        await local.PutAsync("public/a.txt", new MemoryStream("hello"u8.ToArray()), new StoragePutOptions("text/plain", Visibility: StorageVisibility.Public));
        using var mounted = await StartAsync(local, mount: "/media");

        var response = await mounted.GetTestClient().GetAsync("/media/a.txt");

        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_explicit_mount_wins_over_the_path_of_the_public_base_url()
    {
        // Behind UsePathBase("/api") or a prefix-stripping proxy the link says /api/media but the app sees /media.
        await PutAsync("public/a.txt", "hello", "text/plain");
        using var mounted = await StartAsync(CreateProviderSharing("https://example.com/api/media"), mount: "media");

        var response = await mounted.GetTestClient().GetAsync("/media/a.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_public_base_url_path_with_a_space_is_matched_decoded()
    {
        // Uri.AbsolutePath is percent-encoded ("/my%20media"); routing matches the decoded request path.
        var spaced = CreateProviderSharing("https://cdn.example.com/my media");
        await PutAsync("public/a.txt", "hello", "text/plain");
        using var mounted = await StartAsync(spaced);

        var response = await mounted.GetTestClient().GetAsync("/my media/a.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/me{dia")]
    [InlineData("/{tenant}/media")]
    public async Task A_mount_that_is_a_route_template_is_refused_at_startup(string mount)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(provider, mount: mount));

        Assert.Contains("mount", error.Message);
    }

    private sealed class NoUserHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
