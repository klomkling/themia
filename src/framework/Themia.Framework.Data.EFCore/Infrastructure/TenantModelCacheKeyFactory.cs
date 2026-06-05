using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Framework.Data.EFCore.Infrastructure;

/// <summary>
/// Custom model cache key factory that includes tenant ID in the cache key.
/// This ensures each tenant gets its own compiled model with correct query filters.
/// </summary>
public sealed class TenantModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        if (context is ThemiaDbContext themiaContext)
        {
            // Include tenant ID in cache key to prevent filter "freezing" bug
            // Each tenant gets its own compiled model with correct tenant filter baked in
            var tenantId = themiaContext.InternalTenantContext?.CurrentTenantId;
            return (context.GetType(), tenantId?.Value ?? string.Empty, designTime);
        }

        return (context.GetType(), designTime);
    }
}
