using Themia.Storage.Local;
using Themia.Storage.S3;

namespace Themia.Storage.Configuration;

/// <summary>Where the public slot lives. The private slot is always Local, so it has no entry here.</summary>
internal enum PublicSlot
{
    Local,
    S3,
}

/// <summary>What the <c>Storage</c> configuration section describes, already validated.</summary>
internal sealed class StorageSettings
{
    public required PublicSlot Public { get; init; }

    /// <summary>The private store; with <see cref="PublicSlot.Local"/> it also carries the public container.</summary>
    public required LocalStorageOptions Local { get; init; }

    /// <summary>The public-only S3 slot; set exactly when <see cref="Public"/> is <see cref="PublicSlot.S3"/>.</summary>
    public S3StorageOptions? S3 { get; init; }

    /// <summary>The base URL for Local presigned transfers, or empty.</summary>
    public string PresignedBaseUrl { get; init; } = string.Empty;

    /// <summary>One line per deprecated key in use; names the keys, never their values.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
