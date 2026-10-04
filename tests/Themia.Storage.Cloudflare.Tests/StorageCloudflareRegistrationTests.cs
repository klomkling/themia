using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Cloudflare.Tests;

/// <summary>Wiring the purge (coord #0153): startup validation, inertness when disabled, the token never logged, and the real graph.</summary>
public sealed class StorageCloudflareRegistrationTests : IDisposable
{
    private const string Token = "cf-secret-token-0123456789";
    private const string SuccessBody = """{"errors":[],"messages":[],"result":{"id":"x"},"success":true}""";
    private static readonly Uri Photo = new("https://img.example.com/photos/a.jpg");

    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-cf-private", Guid.NewGuid().ToString("N"));
    private readonly string publicRoot = Path.Combine(Path.GetTempPath(), "themia-cf-public", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        if (Directory.Exists(publicRoot)) Directory.Delete(publicRoot, recursive: true);
    }

    private static ServiceProvider Build(Action<CloudflarePurgeOptions> configure, HttpMessageHandler? handler = null, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddThemiaStorageCloudflarePurge(configure);
        if (handler is not null)
        {
            services.AddHttpClient(CloudflareCdnPurger.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        }

        more?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void Enabled(CloudflarePurgeOptions o)
    {
        o.Enabled = true;
        o.ZoneId = "zone-1";
        o.ApiToken = Token;
    }

    [Fact]
    public void Enabled_with_a_blank_zone_fails_host_start()
    {
        using var provider = Build(o => { Enabled(o); o.ZoneId = " "; });

        var thrown = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("ZoneId", thrown.Message);
    }

    [Fact]
    public void Enabled_with_a_blank_token_fails_host_start_without_echoing_a_token()
    {
        using var provider = Build(o => { Enabled(o); o.ApiToken = string.Empty; });

        var thrown = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("ApiToken", thrown.Message);
    }

    [Theory]
    [InlineData("token\n")]
    [InlineData("token\r\n")]
    [InlineData("tok en")]
    [InlineData("token\t")]
    public void A_token_with_whitespace_or_control_characters_fails_host_start(string token)
    {
        // A trailing newline from a secret file passed the blank check and then threw FormatException
        // ("New-line or NUL characters are not allowed in header values") on every purge.
        using var provider = Build(o => { Enabled(o); o.ApiToken = token; });

        var thrown = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("ApiToken", thrown.Message);
        Assert.DoesNotContain("token", thrown.Message.Replace("ApiToken", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void A_plain_token_passes_validation()
    {
        using var provider = Build(o => { Enabled(o); o.ApiToken = "abc.DEF_123-xyz"; });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Disabled_with_blank_settings_starts_and_so_do_the_bare_defaults()
    {
        using var disabled = Build(o => { o.Enabled = false; o.ZoneId = string.Empty; o.ApiToken = string.Empty; });
        using var defaults = Build(_ => { });

        disabled.GetRequiredService<IStartupValidator>().Validate();
        defaults.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public async Task Disabled_makes_no_http_call_and_does_not_throw()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        using var provider = Build(o => o.Enabled = false, handler);

        await provider.GetRequiredService<ICdnPurger>().PurgeAsync(Photo);

        Assert.Empty(handler.Requests);
        Assert.IsNotType<CloudflareCdnPurger>(provider.GetRequiredService<ICdnPurger>());
    }

    [Fact]
    public void Enabled_resolves_the_cloudflare_purger()
    {
        using var provider = Build(Enabled);

        Assert.IsType<CloudflareCdnPurger>(provider.GetRequiredService<ICdnPurger>());
    }

    [Fact]
    public void The_named_client_has_the_api_base_address_and_a_fifteen_second_timeout()
    {
        using var provider = Build(Enabled);

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(CloudflareCdnPurger.HttpClientName);

        Assert.Equal(CloudflareCdnPurger.ApiBaseAddress, client.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
    }

    [Fact]
    public async Task The_token_never_reaches_a_trace_level_log()
    {
        var capture = new CapturingLoggerProvider();
        using var provider = Build(Enabled, new RecordingHandler(HttpStatusCode.OK, SuccessBody),
            services => services.AddLogging(b => { b.AddProvider(capture); b.SetMinimumLevel(LogLevel.Trace); }));

        await provider.GetRequiredService<ICdnPurger>().PurgeAsync(Photo);

        Assert.NotEmpty(capture.AllMessages);
        Assert.DoesNotContain(capture.AllMessages, m => m.Contains(Token));
    }

    [Fact]
    public async Task The_token_stays_redacted_even_if_the_frameworks_default_stops_redacting()
    {
        // Built by hand: the non-redacting default must be registered BEFORE the extension so the extension's
        // per-client redaction runs after it and wins. Pins the explicit RedactLoggedHeaders call.
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.ConfigureAll<HttpClientFactoryOptions>(o => o.ShouldRedactHeaderValue = _ => false);
        services.AddThemiaStorageCloudflarePurge(Enabled);
        services.AddHttpClient(CloudflareCdnPurger.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(HttpStatusCode.OK, SuccessBody));
        services.AddLogging(b => { b.AddProvider(capture); b.SetMinimumLevel(LogLevel.Trace); });
        using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<ICdnPurger>().PurgeAsync(Photo);

        Assert.NotEmpty(capture.AllMessages);
        Assert.DoesNotContain(capture.AllMessages, m => m.Contains(Token));
    }

    [Fact]
    public async Task The_token_stays_redacted_when_a_later_registration_turns_header_redaction_off()
    {
        // The redaction is applied after every ordinary Configure, so an app-wide ConfigureAll registered
        // AFTER AddThemiaStorageCloudflarePurge cannot switch it off by accident.
        var capture = new CapturingLoggerProvider();
        using var provider = Build(Enabled, new RecordingHandler(HttpStatusCode.OK, SuccessBody), services =>
        {
            services.AddLogging(b => { b.AddProvider(capture); b.SetMinimumLevel(LogLevel.Trace); });
            services.ConfigureAll<HttpClientFactoryOptions>(o => o.ShouldRedactHeaderValue = _ => false);
        });

        await provider.GetRequiredService<ICdnPurger>().PurgeAsync(Photo);

        Assert.NotEmpty(capture.AllMessages);
        Assert.DoesNotContain(capture.AllMessages, m => m.Contains(Token));
    }

    [Fact]
    public async Task Control_the_same_capture_does_see_the_token_once_redaction_is_overridden()
    {
        // If this fails, the log never carries request headers at all, which makes the test above vacuous:
        // rethink how it observes the header before trusting it.
        var capture = new CapturingLoggerProvider();
        using var provider = Build(Enabled, new RecordingHandler(HttpStatusCode.OK, SuccessBody), services =>
        {
            services.AddLogging(b => { b.AddProvider(capture); b.SetMinimumLevel(LogLevel.Trace); });
            services.PostConfigure<HttpClientFactoryOptions>(CloudflareCdnPurger.HttpClientName, o => o.ShouldRedactHeaderValue = _ => false);
        });

        await provider.GetRequiredService<ICdnPurger>().PurgeAsync(Photo);

        Assert.Contains(capture.AllMessages, m => m.Contains(Token));
    }

    [Fact]
    public async Task The_default_graph_purges_a_public_delete_and_leaves_a_private_delete_alone()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        using var provider = Build(Enabled, handler, services =>
        {
            services.AddSingleton(_ => new LocalStorageProvider(new LocalStorageOptions
            {
                RootPath = Path.Combine(root, "unused-public-private-root"),
                PublicRootPath = publicRoot,
                PublicBaseUrl = "https://cdn.example.com/media",
                SigningKey = "test-signing-key-at-least-32-characters-long",
            }));
            services.AddThemiaSplitStorage(
                sp => new PurgingStorageProvider(sp.GetRequiredService<LocalStorageProvider>(), sp.GetRequiredService<ICdnPurger>()),
                _ => new LocalStorageProvider(new LocalStorageOptions { RootPath = root, SigningKey = "test-signing-key-at-least-32-characters-long" }));
        });
        var storage = provider.GetRequiredService<IStorageProvider>();
        await storage.PutAsync("public/a.jpg", new MemoryStream([1]), new StoragePutOptions("image/jpeg", Visibility: StorageVisibility.Public));
        await storage.PutAsync("docs/id.pdf", new MemoryStream([1]), new StoragePutOptions("application/pdf"));

        await storage.DeleteAsync("public/a.jpg");
        await storage.DeleteAsync("docs/id.pdf");

        var request = Assert.Single(handler.Requests);
        Assert.Contains("https://cdn.example.com/media/a.jpg", request.Body);
        Assert.False(await storage.ExistsAsync("public/a.jpg"));
        Assert.False(await storage.ExistsAsync("docs/id.pdf"));
    }

    [Fact]
    public async Task A_decorator_as_the_sole_provider_purges_through_the_same_registration()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        using var provider = Build(Enabled, handler, services =>
        {
            services.AddSingleton(_ => new LocalStorageProvider(new LocalStorageOptions
            {
                RootPath = root,
                PublicRootPath = publicRoot,
                PublicBaseUrl = "https://cdn.example.com/media",
                SigningKey = "test-signing-key-at-least-32-characters-long",
            }));
            services.AddSingleton<IStorageProvider>(sp =>
                new PurgingStorageProvider(sp.GetRequiredService<LocalStorageProvider>(), sp.GetRequiredService<ICdnPurger>()));
        });

        await provider.GetRequiredService<IStorageProvider>().DeleteAsync("public/a.jpg");

        Assert.Single(handler.Requests);
    }
}
