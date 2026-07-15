using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Storage;

namespace Themia.Modules.Storage;

/// <summary>Maps a caller's logical key to the physical key handed to <see cref="Themia.Storage.IStorageProvider"/>,
/// prefixing it with the tenant id (or the platform prefix), and — for a public object — with the reserved
/// visibility prefix that selects the public container. Centralizing both prefixes here is what makes
/// tenant isolation and container routing hold by construction.</summary>
public static class StorageScope
{
    /// <summary>The prefix for platform (tenant-less) objects.</summary>
    public const string PlatformPrefix = "_platform";

    /// <summary>The reserved tenant id that would collide with the public namespace.</summary>
    private const string PublicReservedTenantId = "public";

    /// <summary>Builds the physical key for a <see cref="StorageVisibility.Private"/> object.</summary>
    /// <param name="tenantId">The owning tenant, or <see langword="null"/> for a platform object.</param>
    /// <param name="logicalKey">The caller's key.</param>
    /// <returns>The physical key <c>{tenant}/{key}</c> (or <c>_platform/{key}</c>).</returns>
    public static string PhysicalKey(TenantId? tenantId, string logicalKey) =>
        PhysicalKey(tenantId, logicalKey, StorageVisibility.Private);

    /// <summary>Builds the physical key for <paramref name="logicalKey"/> under <paramref name="tenantId"/>,
    /// addressing the container selected by <paramref name="visibility"/>.</summary>
    /// <param name="tenantId">The owning tenant, or <see langword="null"/> for a platform object.</param>
    /// <param name="logicalKey">The caller's key (validated — rejected if it has a leading '/' or a '..' segment).</param>
    /// <param name="visibility">Which container the object lives in.</param>
    /// <returns><c>{tenant}/{key}</c> for a private object — <b>byte-identical to the pre-0.9.0 key, so no
    /// stored blob ever moves</b> — and <c>public/{tenant}/{key}</c> for a public one.</returns>
    /// <exception cref="ArgumentException">The key is blank, absolute, or contains a '..' segment, or the
    /// tenant id equals a reserved prefix.</exception>
    public static string PhysicalKey(TenantId? tenantId, string logicalKey, StorageVisibility visibility)
    {
        var normalized = StorageKey.NormalizeAndValidate(logicalKey);

        // A tenant whose id equals a reserved prefix would collide at the blob layer, breaking isolation:
        // '_platform' would collide with platform objects, and 'public' would place its PRIVATE objects at
        // public/{key} — inside the public namespace, world-readable. Reject both.
        if (tenantId is { } t)
        {
            if (string.Equals(t.Value, PlatformPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Tenant id '{PlatformPrefix}' is reserved for platform objects.", nameof(tenantId));
            }

            if (string.Equals(t.Value, PublicReservedTenantId, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Tenant id '{PublicReservedTenantId}' is reserved: it names the public container.", nameof(tenantId));
            }
        }

        var prefix = tenantId?.Value ?? PlatformPrefix;
        var scoped = $"{prefix}/{normalized}";
        return visibility == StorageVisibility.Public ? StorageKey.PublicPrefix + scoped : scoped;
    }
}
