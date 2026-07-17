namespace Themia.Modules.Storage;

/// <summary>Options for the Storage module.</summary>
public sealed class StorageModuleOptions
{
    /// <summary>The named connection string used to run the FluentMigrator schema.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>The maximum size of a single object, in bytes (default 100 MiB).</summary>
    public long MaxObjectSizeBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>The default per-tenant quota, in bytes (default 5 GiB).</summary>
    public long DefaultTenantQuotaBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>An allowlist of accepted content types. <see langword="null"/> or empty allows any type.</summary>
    public IReadOnlyList<string>? AllowedContentTypes { get; set; }

    /// <summary>How long a public object may be cached by browsers and CDNs (default 1 day). Public media
    /// is not sensitive — the deliberate opposite of the dashboards' <c>no-store</c>.</summary>
    public TimeSpan PublicCacheMaxAge { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Validates the options; throws when a value is out of range.</summary>
    /// <exception cref="ArgumentException">The connection-string name is null or whitespace.</exception>
    /// <exception cref="System.ArgumentOutOfRangeException">A numeric option is less than 1.</exception>
    public void Validate()
    {
        if (MaxObjectSizeBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxObjectSizeBytes), MaxObjectSizeBytes, "Must be at least 1 byte.");
        if (DefaultTenantQuotaBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(DefaultTenantQuotaBytes), DefaultTenantQuotaBytes, "Must be at least 1 byte.");
        if (PublicCacheMaxAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PublicCacheMaxAge), PublicCacheMaxAge, "Must not be negative.");
        if (string.IsNullOrWhiteSpace(ConnectionStringName))
            throw new ArgumentException("Must not be null or whitespace.", nameof(ConnectionStringName));
    }
}
