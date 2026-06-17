namespace Themia.Storage.Local;

/// <summary>Options for the filesystem-backed <see cref="LocalStorageProvider"/>.</summary>
public sealed class LocalStorageOptions
{
    /// <summary>The root directory under which objects are stored. Created on first write if absent.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>The HMAC key used to sign Local presigned URLs. Must be set to use presigned URLs.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Validates that required options are set, failing fast at composition time.</summary>
    /// <exception cref="ArgumentException">Thrown when <see cref="RootPath"/> or <see cref="SigningKey"/> is null or whitespace.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath)) throw new ArgumentException("RootPath must be set.", nameof(RootPath));
        if (string.IsNullOrWhiteSpace(SigningKey)) throw new ArgumentException("SigningKey must be set (required to issue/verify Local presigned download/upload URLs).", nameof(SigningKey));
    }
}
