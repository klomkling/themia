using Microsoft.Extensions.DependencyInjection;
using Themia.Mediator.Abstractions;

namespace Themia.MultiTenancy.Mediator;

/// <summary>
/// Registration helpers for the tenant-presence guard behavior.
/// </summary>
public static class TenantGuardServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TenantGuardBehavior{TRequest, TResponse}"/> as a Mediator pipeline behavior.
    /// Call this so the guard runs early in the pipeline (execution order follows registration order),
    /// before validation and the handler.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration for <see cref="TenantGuardOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddThemiaTenantGuard(
        this IServiceCollection services,
        Action<TenantGuardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<TenantGuardOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TenantGuardBehavior<,>));
        return services;
    }
}
