using Microsoft.Extensions.Options;

namespace Themia.Storage.Urls;

/// <inheritdoc cref="IStorageUrlService" />
public sealed class StorageUrlService : IStorageUrlService
{
    private readonly IStorageProvider provider;
    private readonly StorageUrlOptions options;

    /// <summary>Creates the service.</summary>
    /// <param name="provider">The storage provider that signs the URL.</param>
    /// <param name="options">Where relative presigned URLs are served.</param>
    public StorageUrlService(IStorageProvider provider, IOptions<StorageUrlOptions> options)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        this.provider = provider;
        this.options = options.Value ?? throw new ArgumentNullException(nameof(options));

        if (this.options.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }
    }

    /// <inheritdoc />
    public async Task<Uri> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "A presigned URL's lifetime must be positive.");
        }

        var url = await provider
            .GetPresignedUrlAsync(key, new PresignedUrlRequest(PresignedUrlOperation.Get, lifetime), cancellationToken)
            .ConfigureAwait(false);

        if (url.IsAbsoluteUri)
        {
            return url;
        }

        if (string.IsNullOrWhiteSpace(options.PresignedBaseUrl))
        {
            throw new InvalidOperationException(
                "The storage provider returned a relative presigned URL, and StorageUrlOptions.PresignedBaseUrl is not set. " +
                "Set it to the absolute URL of the mount passed to MapThemiaLocalStorage, e.g. https://api.example.com/api/v1/storage.");
        }

        return Join(options.PresignedBaseUrl, url);
    }

    /// <summary>Appends a relative URL to a base, keeping every segment of the base.</summary>
    /// <remarks>
    /// Deliberately not <c>new Uri(baseUri, relative)</c>. That resolves the reference RFC-3986-style: with a base
    /// of <c>https://host/api/v1/storage</c> — no trailing slash — the last segment is <b>replaced</b>, giving
    /// <c>https://host/api/v1/_local/get</c>. Every link then 404s, and the only difference from a working
    /// configuration is one character.
    /// </remarks>
    internal static Uri Join(string baseUrl, Uri relative) =>
        new($"{baseUrl.TrimEnd('/')}/{relative.OriginalString.TrimStart('/')}", UriKind.Absolute);
}
