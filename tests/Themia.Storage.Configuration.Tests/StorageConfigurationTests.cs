using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Themia.Storage.Urls;
using Xunit;

namespace Themia.Storage.Configuration.Tests;

public sealed class StorageConfigurationTests : IDisposable
{
    private const string SecretKey = "sentinel-secret-key-0123456789";
    private const string AccessKey = "sentinel-access-key-0123456789";
    private const string SigningKey = "sentinel-signing-key-0123456789-abcdef";

    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "themia-storage-config", Guid.NewGuid().ToString("N"));
    private readonly CapturingLoggerProvider logs = new();

    public StorageConfigurationTests() => Directory.CreateDirectory(contentRoot);

    public void Dispose() => Directory.Delete(contentRoot, recursive: true);

    private static Dictionary<string, string?> Local() => new()
    {
        ["Storage:Local:RootPath"] = "private",
        ["Storage:Local:SigningKey"] = SigningKey,
    };

    private static Dictionary<string, string?> S3(Dictionary<string, string?>? extra = null)
    {
        var settings = Local();
        settings["Storage:Public:Provider"] = "R2";
        settings["Storage:S3:PublicBucketName"] = "photos";
        settings["Storage:S3:PublicBaseUrl"] = "https://img.example.com";
        settings["Storage:S3:ServiceUrl"] = "https://acct.r2.cloudflarestorage.com";
        settings["Storage:S3:AccessKey"] = AccessKey;
        settings["Storage:S3:SecretKey"] = SecretKey;
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    private async Task<IHost> Start(Dictionary<string, string?> settings, Action<IServiceCollection>? more = null)
    {
        var host = new HostBuilder()
            .UseContentRoot(contentRoot)
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(settings))
            .ConfigureLogging(l => l.AddProvider(logs))
            .ConfigureServices((context, services) =>
            {
                services.AddThemiaStorageFromConfiguration(context.Configuration);
                more?.Invoke(services);
            })
            .Build();
        try
        {
            await host.StartAsync();
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return host;
    }

    private async Task<string> FailureOf(Dictionary<string, string?> settings)
    {
        var error = await StopsTheHost(() => Start(settings));
        Assert.DoesNotContain(SecretKey, error.Message);
        Assert.DoesNotContain(AccessKey, error.Message);
        Assert.DoesNotContain(SigningKey, error.Message);
        return error.Message;
    }

    // Whichever start-up check runs first reports: the reader's own InvalidOperationException or the validator's.
    private static async Task<Exception> StopsTheHost(Func<Task<IHost>> start)
    {
        var error = await Record.ExceptionAsync(async () => (await start()).Dispose());
        Assert.NotNull(error);
        Assert.True(error is InvalidOperationException or OptionsValidationException, $"{error.GetType().Name} was thrown");
        return error;
    }

    [Fact]
    public async Task Local_is_the_default_public_slot_and_one_provider_holds_both_containers()
    {
        var settings = Local();
        settings["Storage:Local:PublicBaseUrl"] = "https://api.example.com/storage/public";

        using var host = await Start(settings);

        var provider = Assert.IsType<LocalStorageProvider>(host.Services.GetRequiredService<IStorageProvider>());
        await provider.PutAsync("public/a.txt", new MemoryStream([1]), new StoragePutOptions("text/plain", Visibility: StorageVisibility.Public));
        Assert.True(File.Exists(Path.Combine(contentRoot, "private", "public", "blobs", "a.txt")));
    }

    [Fact]
    public async Task A_relative_root_is_resolved_against_the_content_root()
    {
        using var host = await Start(Local());

        var provider = host.Services.GetRequiredService<IStorageProvider>();
        await provider.PutAsync("verifications/id.pdf", new MemoryStream([1]), new StoragePutOptions("application/pdf"));
        Assert.True(File.Exists(Path.Combine(contentRoot, "private", "blobs", "verifications", "id.pdf")));
    }

    [Fact]
    public async Task An_S3_public_slot_builds_the_split_and_private_keys_land_on_local()
    {
        using var host = await Start(S3());

        var provider = Assert.IsType<SplitStorageProvider>(host.Services.GetRequiredService<IStorageProvider>());
        await provider.PutAsync("verifications/id.pdf", new MemoryStream([1]), new StoragePutOptions("application/pdf"));
        Assert.True(File.Exists(Path.Combine(contentRoot, "private", "blobs", "verifications", "id.pdf")));
    }

    [Theory]
    [InlineData("Storage:Private:Provider", "S3")]
    [InlineData("Storage:Private:Provider", "R2")]
    [InlineData("Storage:Private:Provider", "Locall")]
    [InlineData("Storage:Provider", "S3")]
    [InlineData("Storage:Provider", "R2")]
    [InlineData("Storage:Provider", "Splitt")]
    [InlineData("Storage:Public:Provider", "Azure")]
    public async Task A_value_that_would_move_private_files_off_local_or_is_a_typo_stops_the_host(string key, string value)
    {
        var settings = S3();
        settings[key] = value;
        if (key == "Storage:Provider")
        {
            settings.Remove("Storage:Public:Provider");
        }

        Assert.Contains(key, await FailureOf(settings));
    }

    [Fact]
    public async Task Storage_S3_BucketName_is_refused_with_a_rename_message()
    {
        var message = await FailureOf(S3(new() { ["Storage:S3:BucketName"] = "private-bucket" }));

        Assert.Contains("Storage:S3:PublicBucketName", message);
    }

    [Theory]
    [InlineData("Storage:S3:AccessKey")]
    [InlineData("Storage:S3:SecretKey")]
    [InlineData("Storage:S3:PublicBucketName")]
    [InlineData("Storage:S3:PublicBaseUrl")]
    [InlineData("Storage:Local:SigningKey")]
    [InlineData("Storage:Local:RootPath")]
    public async Task A_missing_required_key_is_named(string key)
    {
        var settings = S3();
        settings.Remove(key);

        var message = await FailureOf(settings);

        Assert.Contains(key.Split(':')[^1], message);
    }

    [Fact]
    public async Task A_public_root_inside_the_private_blobs_directory_stops_the_host()
    {
        var settings = Local();
        settings["Storage:Local:PublicBaseUrl"] = "https://api.example.com/p";
        settings["Storage:Local:PublicRootPath"] = "private/blobs/pub";

        Assert.Contains("PublicRootPath", await FailureOf(settings));
    }

    [Fact]
    public async Task S3_keys_with_a_local_public_slot_are_dead_configuration_and_stop_the_host()
    {
        var settings = S3();
        settings.Remove("Storage:Public:Provider");

        Assert.Contains("Storage:S3", await FailureOf(settings));
    }

    [Fact]
    public async Task Local_public_keys_with_an_S3_public_slot_stop_the_host()
    {
        var message = await FailureOf(S3(new() { ["Storage:Local:PublicBaseUrl"] = "https://api.example.com/p" }));

        Assert.Contains("PublicBaseUrl", message);
    }

    [Fact]
    public async Task An_unknown_key_under_a_storage_section_is_refused()
    {
        var settings = S3();
        settings["Storage:Public:Provder"] = "S3";

        Assert.Contains("Storage:Public:Provder", await FailureOf(settings));
    }

    [Fact]
    public async Task Propertiezy_style_split_is_accepted_with_one_warning_naming_keys_only()
    {
        var settings = S3();
        settings.Remove("Storage:Public:Provider");
        settings["Storage:Provider"] = "Split";

        using var host = await Start(settings);

        Assert.IsType<SplitStorageProvider>(host.Services.GetRequiredService<IStorageProvider>());
        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("Storage:Provider", warning);
        Assert.Contains(StorageSettingsReader.LegacyKeysRemovedIn, warning);
        Assert.All(new[] { SecretKey, AccessKey, SigningKey }, secret => Assert.DoesNotContain(secret, warning));
    }

    [Fact]
    public async Task The_legacy_provider_key_next_to_the_public_provider_key_stops_the_host()
    {
        var message = await FailureOf(S3(new() { ["Storage:Provider"] = "Split" }));

        Assert.Contains("Storage:Provider", message);
    }

    [Fact]
    public async Task The_legacy_uploads_directory_is_the_root_with_a_warning()
    {
        var settings = Local();
        settings.Remove("Storage:Local:RootPath");
        settings["Storage:Local:UploadsDirectory"] = "uploads";

        using var host = await Start(settings);

        var provider = host.Services.GetRequiredService<IStorageProvider>();
        await provider.PutAsync("a/b.txt", new MemoryStream([1]), new StoragePutOptions("text/plain"));
        Assert.True(File.Exists(Path.Combine(contentRoot, "uploads", "blobs", "a", "b.txt")));
        Assert.Contains("UploadsDirectory", Assert.Single(logs.Warnings));
    }

    [Fact]
    public async Task The_legacy_download_base_url_feeds_the_presigned_base_url_with_a_warning()
    {
        var settings = Local();
        settings["Storage:DownloadBaseUrl"] = "https://api.example.com/api/v1/storage";

        using var host = await Start(settings);

        Assert.Equal("https://api.example.com/api/v1/storage", host.Services.GetRequiredService<IOptions<StorageUrlOptions>>().Value.PresignedBaseUrl);
        Assert.Contains("DownloadBaseUrl", Assert.Single(logs.Warnings));
    }

    [Fact]
    public async Task Different_download_and_presigned_base_urls_stop_the_host()
    {
        var settings = Local();
        settings["Storage:DownloadBaseUrl"] = "https://a.example.com/s";
        settings["Storage:Local:PresignedBaseUrl"] = "https://b.example.com/s";

        Assert.Contains("PresignedBaseUrl", await FailureOf(settings));
    }

    [Fact]
    public async Task Secrets_are_never_logged()
    {
        using var host = await Start(S3(new() { ["Storage:DownloadBaseUrl"] = "https://api.example.com/s" }));

        Assert.All(new[] { SecretKey, AccessKey, SigningKey }, secret => Assert.DoesNotContain(secret, logs.All));
    }

    [Fact]
    public void A_second_provider_registration_is_refused()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStorageProvider>(_ => null!);

        Assert.Throws<InvalidOperationException>(() => services.AddThemiaStorageFromConfiguration(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task A_provider_registered_afterwards_that_replaces_this_one_stops_the_host()
    {
        var error = await StopsTheHost(() => Start(Local(), services => services.AddSingleton<IStorageProvider>(new LocalStorageProvider(new LocalStorageOptions
        {
            RootPath = Path.Combine(contentRoot, "other"),
            SigningKey = SigningKey,
        }))));

        Assert.Contains("replaced", error.Message);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> entries = [];

        public IEnumerable<string> Warnings => entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

        public string All => string.Join('\n', entries.Select(e => e.Message));

        public ILogger CreateLogger(string categoryName) => new Logger(entries);

        public void Dispose()
        {
        }

        private sealed class Logger(List<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add((logLevel, formatter(state, exception) + exception));
        }
    }
}
