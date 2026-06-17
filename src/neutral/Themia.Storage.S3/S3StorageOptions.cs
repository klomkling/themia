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
}
