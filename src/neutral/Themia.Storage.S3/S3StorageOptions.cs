namespace Themia.Storage.S3;

/// <summary>Options for the S3-compatible <see cref="S3StorageProvider"/> (AWS S3, Cloudflare R2, MinIO).</summary>
public sealed class S3StorageOptions
{
    /// <summary>The bucket objects are stored in.</summary>
    public string BucketName { get; set; } = string.Empty;

    /// <summary>The AWS region system name (e.g. <c>us-east-1</c>). Ignored when <see cref="ServiceUrl"/> is set.</summary>
    public string? Region { get; set; }

    /// <summary>The access key id. When null, the AWS default credential chain is used.</summary>
    public string? AccessKey { get; set; }

    /// <summary>The secret access key. When null, the AWS default credential chain is used.</summary>
    public string? SecretKey { get; set; }

    /// <summary>A custom service endpoint (set for Cloudflare R2 / MinIO / any S3-compatible server).</summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>Whether to force path-style addressing (required for R2 and MinIO).</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>The bucket for <see cref="Themia.Storage.StorageVisibility.Public"/> objects. It must be a
    /// <b>public-read</b> bucket (or one fronted by a public custom domain / CDN). Leave unset to disable
    /// the public container. Separate buckets are not a style choice: R2 has no per-object ACL, and S3
    /// Object Ownership defaults to <c>bucket owner enforced</c>, which disables object ACLs entirely — so
    /// "public" can only mean "in the public bucket".</summary>
    public string PublicBucketName { get; set; } = string.Empty;

    /// <summary>The <b>absolute</b> base URL public objects are served from — the public bucket's custom
    /// domain or the CDN in front of it (e.g. <c>https://cdn.example.com</c>). Required when
    /// <see cref="PublicBucketName"/> is set. Bytes are served straight from the bucket; the app is never
    /// in the request path.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Validates the public-container options, failing fast at composition time.</summary>
    /// <exception cref="ArgumentException">Only one of <see cref="PublicBucketName"/> /
    /// <see cref="PublicBaseUrl"/> is set, the base URL is not an absolute http(s) URL, or the public
    /// bucket equals the private one.</exception>
    public void Validate()
    {
        var hasBucket = !string.IsNullOrWhiteSpace(PublicBucketName);
        var hasBaseUrl = !string.IsNullOrWhiteSpace(PublicBaseUrl);

        if (!hasBucket && !hasBaseUrl)
        {
            return; // public container disabled
        }

        // Both-or-neither: a base URL with no bucket (or vice versa) is a half-configured broken state.
        if (hasBucket != hasBaseUrl)
        {
            throw new ArgumentException(
                "PublicBucketName and PublicBaseUrl must be set together (a public container needs both a bucket and an absolute base URL), or both left empty.",
                nameof(PublicBucketName));
        }

        // Uri.TryCreate(..., UriKind.Absolute, ...) alone is not enough: on Unix it parses a rooted path
        // like "/media" as file:///media, so require an http/https scheme — the ezy-assets relative-URL bug.
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "PublicBaseUrl must be an ABSOLUTE http(s) url (e.g. https://cdn.example.com) when PublicBucketName is set.",
                nameof(PublicBaseUrl));
        }

        // Bucket names are case-sensitive AWS identifiers, so Ordinal is correct here (unlike filesystem
        // paths). A public and private object sharing a bucket would break the isolation this exists for.
        if (string.Equals(BucketName, PublicBucketName, StringComparison.Ordinal))
        {
            throw new ArgumentException("PublicBucketName must differ from BucketName; public and private objects cannot share a container.", nameof(PublicBucketName));
        }
    }
}
