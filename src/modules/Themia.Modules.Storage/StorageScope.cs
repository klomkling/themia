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
    /// <param name="logicalKey">The caller's key (validated — rejected if it has a leading '/' or a '..' segment).</param>
    /// <returns>The physical key <c>{tenant}/{key}</c> (or <c>_platform/{key}</c>).</returns>
    /// <exception cref="ArgumentException">The key is blank, absolute, or contains a '..' segment,
    /// or the tenant id equals the reserved platform prefix.</exception>
    public static string PhysicalKey(TenantId? tenantId, string logicalKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalKey);
        var normalized = logicalKey.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Split('/').Contains(".."))
        {
            throw new ArgumentException($"Invalid object key '{logicalKey}': absolute paths and '..' segments are not allowed.", nameof(logicalKey));
        }

        // A tenant whose id equals the platform prefix would collide with platform objects at the
        // blob layer, breaking isolation — reject it.
        if (tenantId is { } t && string.Equals(t.Value, PlatformPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Tenant id '{PlatformPrefix}' is reserved for platform objects.", nameof(tenantId));
        }

        var prefix = tenantId?.Value ?? PlatformPrefix;
        return $"{prefix}/{normalized}";
    }
}
