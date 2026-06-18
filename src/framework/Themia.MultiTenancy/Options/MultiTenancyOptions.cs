namespace Themia.MultiTenancy;

/// <summary>
/// Configuration for tenant resolution.
/// </summary>
public sealed class MultiTenancyOptions
{
    /// <summary>
    /// Header name to inspect for tenant ID (default: X-Tenant-ID).
    /// </summary>
    public string HeaderName { get; set; } = "X-Tenant-ID";

    /// <summary>
    /// Claim type inspected for the tenant identifier by the claims strategy (default: tenant_id).
    /// </summary>
    public string ClaimType { get; set; } = "tenant_id";

    /// <summary>
    /// Optional path prefix segment used for tenant identification (e.g., /{tenantId}/api).
    /// </summary>
    public string? PathPrefix { get; set; }

    /// <summary>
    /// Optional default tenant identifier to fall back to when none is resolved.
    /// </summary>
    public string? DefaultTenantIdentifier { get; set; }

    /// <summary>
    /// Whether to register the built-in strategies (header, path, default) automatically.
    /// Default is true. Set to false if you want to register only custom strategies.
    /// </summary>
    /// <remarks>
    /// When false, you must manually register strategies via the configure callback,
    /// otherwise no tenant resolution will occur.
    /// </remarks>
    public bool UseDefaultStrategies { get; set; } = true;

    /// <summary>
    /// Copies every configurable value from this instance onto <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Lives next to the property declarations so adding a new option means updating the copy in the
    /// same place — the previous field-by-field copy lived in the DI registration and silently dropped
    /// newly added options. Keep this in sync with the properties above.
    /// </remarks>
    /// <param name="target">The options instance to copy values onto.</param>
    internal void CopyTo(MultiTenancyOptions target)
    {
        target.HeaderName = HeaderName;
        target.ClaimType = ClaimType;
        target.PathPrefix = PathPrefix;
        target.DefaultTenantIdentifier = DefaultTenantIdentifier;
        target.UseDefaultStrategies = UseDefaultStrategies;
    }
}
