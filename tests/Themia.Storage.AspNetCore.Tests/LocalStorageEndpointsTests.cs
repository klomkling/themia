using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Themia.Storage.Local;
using Themia.Storage.Urls;
using Xunit;

namespace Themia.Storage.AspNetCore.Tests;

/// <summary>
/// The Local presigned-download route (coord #0134): it must serve a valid link to a caller with no session,
/// refuse everything else with one answer, and never let an uploaded file run script on the API's origin.
/// </summary>
public sealed class LocalStorageEndpointsTests : IAsyncLifetime
{
    private const string SigningKey = "test-signing-key-that-is-long-enough-for-hmac";
    private const string Mount = "/api/v1/storage";

    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-aspnetcore-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
    private LocalStorageProvider provider = null!;
    private IHost host = null!;
    private HttpClient client = null!;

    public async Task InitializeAsync()
    {
        provider = new LocalStorageProvider(new LocalStorageOptions { RootPath = root, SigningKey = SigningKey });
        host = await StartAsync();
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

    private Task<IHost> StartAsync(
        Action<IServiceCollection>? services = null, Action<IApplicationBuilder>? pipeline = null, bool registerSigner = true) =>
        new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton<TimeProvider>(clock);
                    s.AddSingleton<IStorageProvider>(provider);
                    if (registerSigner)
                    {
                        s.AddSingleton(new LocalUrlSigner(SigningKey));
                    }

                    services?.Invoke(s);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    pipeline?.Invoke(app);
                    app.UseEndpoints(e => e.MapThemiaLocalStorage(Mount));
                }))
            .StartAsync();

    private async Task<string> SignedUrlAsync(string key, TimeSpan? lifetime = null, PresignedUrlOperation op = PresignedUrlOperation.Get)
    {
        var relative = await provider.GetPresignedUrlAsync(key, new PresignedUrlRequest(op, lifetime ?? TimeSpan.FromMinutes(5)));
        return $"{Mount}/{relative}";
    }

    private Task PutAsync(string key, string body, string contentType) =>
        provider.PutAsync(key, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body)), new StoragePutOptions(contentType));

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("sandbox", string.Join(",", response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", string.Join(",", response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-store, private", string.Join(", ", response.Headers.CacheControl!.ToString().Split(", ").Order()));
    }

    // ---- serving ------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_link_streams_the_object_with_its_type()
    {
        await PutAsync("docs/a.txt", "hello", "text/plain");

        var response = await client.GetAsync(await SignedUrlAsync("docs/a.txt"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task An_uploaded_svg_is_served_in_a_sandbox()
    {
        // nosniff does nothing for a file whose declared type is already dangerous; the sandbox is what stops
        // this script running on the API's origin.
        await PutAsync("docs/x.svg", "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>", "image/svg+xml");

        var response = await client.GetAsync(await SignedUrlAsync("docs/x.svg"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task A_valid_link_to_a_missing_object_is_404()
    {
        var response = await client.GetAsync(await SignedUrlAsync("docs/never-written.txt"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- refusals -----------------------------------------------------------------------------

    [Fact]
    public async Task A_tampered_token_is_a_bare_403_carrying_the_security_headers()
    {
        await PutAsync("docs/a.txt", "hello", "text/plain");
        var url = await SignedUrlAsync("docs/a.txt");
        var tampered = url[..^1] + (url[^1] == 'A' ? 'B' : 'A');

        var response = await client.GetAsync(tampered);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        AssertSecurityHeaders(response);
    }

    [Fact]
    public async Task An_expired_link_is_403()
    {
        // Signed directly with an expiry already past — the provider stamps expiry from the system clock, so
        // that is the only honest way to produce an expired link.
        await PutAsync("docs/a.txt", "hello", "text/plain");
        var token = new LocalUrlSigner(SigningKey).Sign("docs/a.txt", PresignedUrlOperation.Get, DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await client.GetAsync($"{Mount}/_local/get?key=docs%2Fa.txt&token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_host_clock_set_in_the_past_does_not_revive_an_expired_link()
    {
        // The provider stamps a link's expiry from the system clock. Checking it against the host's
        // TimeProvider instead — a FakeTimeProvider starts in the year 2000 — would make every expired link
        // valid for ever. Signing and checking must read the same clock.
        await PutAsync("docs/a.txt", "hello", "text/plain");
        var token = new LocalUrlSigner(SigningKey).Sign("docs/a.txt", PresignedUrlOperation.Get, DateTimeOffset.UtcNow.AddMinutes(-1));
        using var pastClockHost = await StartAsync(services: s => s.AddSingleton<TimeProvider>(new FakeTimeProvider()));

        var response = await pastClockHost.GetTestClient()
            .GetAsync($"{Mount}/_local/get?key=docs%2Fa.txt&token={Uri.EscapeDataString(token)}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_host_clock_set_in_the_future_does_not_refuse_a_fresh_link()
    {
        await PutAsync("docs/a.txt", "hello", "text/plain");
        using var futureClockHost = await StartAsync(
            services: s => s.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow.AddYears(1))));

        var response = await futureClockHost.GetTestClient().GetAsync(await SignedUrlAsync("docs/a.txt"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_upload_token_does_not_open_a_download()
    {
        // The token signs the operation as well as the key; a leaked PUT link must not read the object.
        await PutAsync("docs/a.txt", "hello", "text/plain");
        var putUrl = await SignedUrlAsync("docs/a.txt", op: PresignedUrlOperation.Put);
        var asGet = putUrl.Replace("_local/put", "_local/get", StringComparison.Ordinal);

        var response = await client.GetAsync(asGet);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_token_for_one_key_does_not_open_another()
    {
        await PutAsync("docs/a.txt", "a", "text/plain");
        await PutAsync("docs/b.txt", "b", "text/plain");
        var forA = await SignedUrlAsync("docs/a.txt");
        var pointedAtB = forA.Replace("docs%2Fa.txt", "docs%2Fb.txt", StringComparison.Ordinal);

        var response = await client.GetAsync(pointedAtB);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?key=docs%2Fa.txt")]
    [InlineData("?token=abc")]
    public async Task A_missing_key_or_token_is_403(string query)
    {
        var response = await client.GetAsync($"{Mount}/_local/get{query}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- anonymous by construction ---------------------------------------------------------------

    [Fact]
    public async Task A_host_requiring_login_everywhere_still_serves_a_valid_link()
    {
        // A FallbackPolicy applies to every endpoint without auth metadata. Not returning the route's group
        // does not protect it from that; AllowAnonymous does.
        await PutAsync("docs/a.txt", "hello", "text/plain");
        using var hardened = await StartAsync(
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

        var response = await hardened.GetTestClient().GetAsync(await SignedUrlAsync("docs/a.txt"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- startup checks -------------------------------------------------------------------------

    [Fact]
    public async Task Mapping_without_a_signer_fails_at_startup()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(registerSigner: false));

        Assert.Contains("LocalUrlSigner", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.example.com/api/v1/storage")]
    [InlineData("https://api.example.com/api/v1/storage/")]
    public async Task A_base_url_ending_with_the_mount_is_accepted(string baseUrl)
    {
        using var ok = await StartAsync(services: s => s.AddThemiaStorageUrls(o => o.PresignedBaseUrl = baseUrl));

        Assert.NotNull(ok);
    }

    [Fact]
    public async Task A_root_mount_accepts_a_root_base_url()
    {
        using var root = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton<IStorageProvider>(provider);
                    s.AddSingleton(new LocalUrlSigner(SigningKey));
                    s.AddThemiaStorageUrls(o => o.PresignedBaseUrl = "http://localhost/");
                })
                .Configure(app => app.UseRouting().UseEndpoints(e => e.MapThemiaLocalStorage("/"))))
            .StartAsync();
        await PutAsync("docs/a.txt", "hello", "text/plain");
        var urls = root.Services.GetRequiredService<IStorageUrlService>();

        var response = await root.GetTestClient().GetAsync(await urls.GetDownloadUrlAsync("docs/a.txt", TimeSpan.FromMinutes(5)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_base_url_differing_only_in_case_is_accepted_because_routing_ignores_case()
    {
        using var ok = await StartAsync(services: s => s.AddThemiaStorageUrls(o => o.PresignedBaseUrl = "https://api.example.com/API/v1/Storage"));

        Assert.NotNull(ok);
    }

    [Fact]
    public async Task A_base_url_for_the_wrong_mount_fails_at_startup()
    {
        // Absolute, well-formed, and every link it produces would 404.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StartAsync(services: s => s.AddThemiaStorageUrls(o => o.PresignedBaseUrl = "https://api.example.com/storage")));

        Assert.Contains(Mount, error.Message, StringComparison.Ordinal);
    }

    // ---- the two halves together --------------------------------------------------------------

    [Fact]
    public async Task A_url_minted_by_the_url_service_is_served_by_the_route()
    {
        await PutAsync("docs/a.txt", "hello", "text/plain");
        using var both = await StartAsync(services: s => s.AddThemiaStorageUrls(o => o.PresignedBaseUrl = "http://localhost" + Mount));
        var urls = both.Services.GetRequiredService<IStorageUrlService>();

        var url = await urls.GetDownloadUrlAsync("docs/a.txt", TimeSpan.FromMinutes(5));
        var response = await both.GetTestClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", await response.Content.ReadAsStringAsync());
    }

    private sealed class NoUserHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }
}
