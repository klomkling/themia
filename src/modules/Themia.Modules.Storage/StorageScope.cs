using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Storage;

/// <summary>Maps a caller's logical key to the physical key handed to <see cref="Themia.Storage.IStorageProvider"/>,
/// prefixing it with the tenant id (or the platform prefix). Centralizing the prefix here is what makes
/// tenant isolation hold by construction — a caller can never reach another tenant's blob.</summary>
public static class StorageScope
{
    /// <summary>The prefix for platform (tenant-less) objects.</summary>
    public const string PlatformPrefix = "_platform";

    /// <summary>Builds the physical key for <paramref name="logicalKey"/> under <paramref name="tenantId"/>.</summary>
    /// <param name="tenantId">The owning tenant, or <see langword="null"/> for a platform object.</param>
    /// <param name="logicalKey">The caller's key (sanitized: no leading '/', no '..' segments).</param>
    /// <returns>The physical key <c>{tenant}/{key}</c> (or <c>_platform/{key}</c>).</returns>
    /// <exception cref="ArgumentException">The key is blank, absolute, or contains a '..' segment.</exception>
    public static string PhysicalKey(TenantId? tenantId, string logicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalKey);
        var normalized = logicalKey.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Contains(".."))
        {
            throw new ArgumentException($"Invalid object key '{logicalKey}': absolute paths and '..' segments are not allowed.", nameof(logicalKey));
        }

        var prefix = tenantId?.Value ?? PlatformPrefix;
        return $"{prefix}/{normalized}";
    }
}
