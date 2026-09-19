using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Storage.Local;
using Themia.Storage.Urls;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// Turning a provider's presigned URL into one a client can open (coord #0134) — including from a background
/// job, which has no request to take a host from.
/// </summary>
public sealed class StorageUrlServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-urls-" + Guid.NewGuid().ToString("N"));
    private readonly LocalStorageProvider local;

    public StorageUrlServiceTests() =>
        local = new LocalStorageProvider(new LocalStorageOptions { RootPath = root, SigningKey = "k-long-enough-for-an-hmac-key-0123" });

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static StorageUrlService Build(IStorageProvider provider, string baseUrl) =>
        new(provider, Options.Create(new StorageUrlOptions { PresignedBaseUrl = baseUrl }));

    [Theory]
    [InlineData("https://api.example.com/api/v1/storage")]
    [InlineData("https://api.example.com/api/v1/storage/")]
    public async Task A_local_url_keeps_every_segment_of_the_base_with_or_without_a_trailing_slash(string baseUrl)
    {
        // new Uri(base, "_local/get?...") replaces the last segment when the base has no trailing slash,
        // producing https://api.example.com/api/v1/_local/get — one character of config from a 404 on every link.
        var url = await Build(local, baseUrl).GetDownloadUrlAsync("docs/a.txt", TimeSpan.FromMinutes(5));

        Assert.True(url.IsAbsoluteUri);
        Assert.StartsWith("https://api.example.com/api/v1/storage/_local/get?key=docs%2Fa.txt&token=", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absolute_url_from_the_provider_is_returned_untouched()
    {
        var signed = new Uri("https://bucket.s3.amazonaws.com/docs/a.txt?X-Amz-Signature=abc");

        var url = await Build(new AbsoluteUrlProvider(signed), "https://api.example.com/api/v1/storage")
            .GetDownloadUrlAsync("docs/a.txt", TimeSpan.FromMinutes(5));

        Assert.Equal(signed, url);
    }

    [Fact]
    public async Task An_absolute_url_needs_no_base_url_at_all()
    {
        var signed = new Uri("https://bucket.s3.amazonaws.com/docs/a.txt?sig=1");

        var url = await Build(new AbsoluteUrlProvider(signed), string.Empty).GetDownloadUrlAsync("docs/a.txt", TimeSpan.FromMinutes(5));

        Assert.Equal(signed, url);
    }

    [Fact]
    public async Task A_relative_url_with_no_base_configured_throws_naming_the_option()
    {
        // Not returned as-is: a relative link travels into an email and breaks there, far from the cause.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(local, string.Empty).GetDownloadUrlAsync("docs/a.txt", TimeSpan.FromMinutes(5)));

        Assert.Contains("PresignedBaseUrl", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_lifetime_is_refused(int minutes)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Build(local, "https://api.example.com/api/v1/storage").GetDownloadUrlAsync("docs/a.txt", TimeSpan.FromMinutes(minutes)));
    }

    [Theory]
    [InlineData("/api/v1/storage")]                                  // rooted path: parses as file:// on Unix
    [InlineData("api.example.com/api/v1/storage")]                   // no scheme
    [InlineData("ftp://api.example.com/api/v1/storage")]             // not http(s)
    [InlineData("https://api.example.com/api/v1/storage?x=1")]       // a query would break the join
    public void A_malformed_base_url_is_refused_at_construction(string baseUrl)
    {
        Assert.Throws<ArgumentException>(() => Build(local, baseUrl));
    }

    [Fact]
    public void A_malformed_base_url_fails_options_validation_at_startup()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IStorageProvider>(local);
        services.AddThemiaStorageUrls(o => o.PresignedBaseUrl = "/api/v1/storage");
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StorageUrlOptions>>().Value);

        Assert.Contains("PresignedBaseUrl", string.Join(" ", error.Failures), StringComparison.Ordinal);
    }

    private sealed class AbsoluteUrlProvider(Uri signed) : IStorageProvider
    {
        public Task<Uri> GetPresignedUrlAsync(string key, PresignedUrlRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(signed);

        public Task<StorageObjectInfo> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StorageObjectInfo?> StatAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Uri GetPublicUrl(string key) => throw new NotSupportedException();
    }
}
