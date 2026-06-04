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
}
