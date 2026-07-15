namespace Themia.Storage.Local;

/// <summary>Options for the filesystem-backed <see cref="LocalStorageProvider"/>.</summary>
public sealed class LocalStorageOptions
{
    /// <summary>The root directory under which objects are stored. Created on first write if absent.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>The HMAC key used to sign Local presigned URLs. Must be set to use presigned URLs.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>The root directory for <see cref="StorageVisibility.Public"/> objects. Leave unset to
    /// disable the public container (any attempt to write or address a public object then throws).
    /// Must be a different directory from <see cref="RootPath"/>.</summary>
    public string PublicRootPath { get; set; } = string.Empty;

    /// <summary>The <b>absolute</b> base URL public objects are served from — for Local this is the app's
    /// origin plus the storage endpoint mount (e.g. <c>https://api.example.com/storage/public</c>).
    /// Required when <see cref="PublicRootPath"/> is set. Resolved at READ time, never persisted: a URL
    /// frozen at upload time cannot survive a CDN swap or a domain change.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Validates that required options are set, failing fast at composition time.</summary>
    /// <exception cref="ArgumentException">Thrown when <see cref="RootPath"/> or <see cref="SigningKey"/> is
    /// null or whitespace, when only one of <see cref="PublicRootPath"/>/<see cref="PublicBaseUrl"/> is set,
    /// when the configured <see cref="PublicBaseUrl"/> is not an absolute http/https url, or when the public
    /// root resolves to the same directory as the private root.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath)) throw new ArgumentException("RootPath must be set.", nameof(RootPath));
        if (string.IsNullOrWhiteSpace(SigningKey)) throw new ArgumentException("SigningKey must be set (required to issue/verify Local presigned download/upload URLs).", nameof(SigningKey));

        var hasRoot = !string.IsNullOrWhiteSpace(PublicRootPath);
        var hasBaseUrl = !string.IsNullOrWhiteSpace(PublicBaseUrl);

        // The public container is both-or-neither: a half-configured state (one set, the other empty) is a
        // silent trap where writes or URL composition break at runtime, so fail fast naming the missing half.
        if (!hasRoot && !hasBaseUrl)
        {
            return;
        }

        if (!hasRoot)
        {
            throw new ArgumentException("PublicRootPath must be set when PublicBaseUrl is set (the public container is both-or-neither).", nameof(PublicRootPath));
        }

        if (!hasBaseUrl)
        {
            throw new ArgumentException("PublicBaseUrl must be set when PublicRootPath is set (the public container is both-or-neither).", nameof(PublicBaseUrl));
        }

        // A relative base URL is the ezy-assets bug in a bottle: it cannot be hot-linked cross-origin, and
        // a photo whose URL was resolved at upload time freezes that relative path in the database forever.
        // Uri.TryCreate(..., UriKind.Absolute, ...) alone does not catch this: on Unix it happily parses a
        // rooted path like "/uploads" as an absolute file:// URI, so the scheme must be checked explicitly.
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var publicBaseUri) ||
            (publicBaseUri.Scheme != Uri.UriSchemeHttp && publicBaseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "PublicBaseUrl must be set to an ABSOLUTE url (e.g. https://api.example.com/storage/public) when PublicRootPath is set.",
                nameof(PublicBaseUrl));
        }

        // Case-insensitive on Windows (NTFS) and macOS (APFS): there "/data/Store" and "/data/store" are the
        // SAME directory, so an ordinal-only compare would wave through a shared container and let a public
        // and private key with the same tail collide — the exact failure this two-container split prevents.
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Path.GetFullPath(RootPath), Path.GetFullPath(PublicRootPath), cmp))
        {
            throw new ArgumentException("PublicRootPath must differ from RootPath; public and private objects cannot share a container.", nameof(PublicRootPath));
        }
    }
}
