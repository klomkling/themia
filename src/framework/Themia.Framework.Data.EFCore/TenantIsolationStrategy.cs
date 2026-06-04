namespace Themia.Framework.Data.EFCore;

/// <summary>
/// Defines strategies for tenant isolation in multi-tenant applications.
/// </summary>
public enum TenantIsolationStrategy
{
    /// <summary>
    /// Each tenant gets its own compiled EF Core model.
    /// Best for applications with &lt; 100 tenants.
    /// Memory: ~500KB per tenant. Performance: Fastest queries.
    /// </summary>
    PerTenantModel,

    /// <summary>
    /// All tenants share one compiled model with runtime tenant resolution via AsyncLocal.
    /// Best for SaaS applications with 1000+ tenants.
    /// Memory: Constant (~500KB total). Performance: Slightly slower due to runtime access.
    /// </summary>
    RuntimeTenantAccess
}
