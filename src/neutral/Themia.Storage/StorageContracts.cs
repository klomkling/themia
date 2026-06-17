namespace Themia.Storage;

/// <summary>The operation a presigned URL authorizes.</summary>
public enum PresignedUrlOperation
{
    /// <summary>Download (HTTP GET).</summary>
    Get,

    /// <summary>Direct upload (HTTP PUT).</summary>
    Put,
}

/// <summary>Options for writing an object.</summary>
/// <param name="ContentType">The MIME content type to store and serve.</param>
/// <param name="Metadata">Optional provider metadata (small string pairs); may be empty.</param>
/// <param name="Overwrite">Whether to overwrite an existing object at the key (default true).</param>
public readonly record struct StoragePutOptions(
    string ContentType,
    IReadOnlyDictionary<string, string>? Metadata = null,
    bool Overwrite = true);

/// <summary>The result of reading an object: an open content stream plus its metadata.</summary>
/// <param name="Content">The object content stream; the caller disposes it.</param>
/// <param name="ContentType">The stored MIME content type.</param>
/// <param name="Length">The object length in bytes.</param>
public sealed record StorageReadResult(Stream Content, string ContentType, long Length);

/// <summary>Metadata about a stored object (no content).</summary>
/// <param name="Key">The object key.</param>
/// <param name="Length">The object length in bytes.</param>
/// <param name="ContentType">The stored MIME content type.</param>
/// <param name="ETag">The backend entity tag, when available.</param>
public sealed record StorageObjectInfo(string Key, long Length, string ContentType, string? ETag);

/// <summary>A request for a presigned URL.</summary>
/// <param name="Operation">Whether the URL authorizes a download or a direct upload.</param>
/// <param name="Expiry">How long the URL stays valid.</param>
/// <param name="ContentType">The content type the upload must declare (Put only); ignored for Get.</param>
public readonly record struct PresignedUrlRequest(
    PresignedUrlOperation Operation,
    TimeSpan Expiry,
    string? ContentType = null);
