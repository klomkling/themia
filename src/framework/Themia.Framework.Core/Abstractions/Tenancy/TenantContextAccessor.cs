namespace Themia.Framework.Core.Abstractions.Tenancy;

/// <summary>
/// Provides ambient tenant information for scenarios (such as query filters)
/// where the active context must be accessed without capturing per-tenant models.
/// </summary>
public static class TenantContextAccessor
{
    private static readonly AsyncLocal<TenantId?> CurrentTenant = new();

    /// <summary>
    /// Gets or sets the current tenant identifier for the ambient execution context.
    /// </summary>
    public static TenantId? CurrentTenantId
    {
        get => CurrentTenant.Value;
        set => CurrentTenant.Value = value;
    }
}
