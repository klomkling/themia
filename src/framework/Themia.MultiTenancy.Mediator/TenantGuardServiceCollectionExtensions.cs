using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Mediator.Abstractions;

namespace Themia.MultiTenancy.Mediator;

/// <summary>
/// Registration helpers for the tenant-presence guard behavior.
/// </summary>
public static class TenantGuardServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TenantGuardBehavior{TRequest, TResponse}"/> as a Mediator pipeline behavior.
    /// Call this before other behavior registrations so the guard runs early — the Themia mediator
    /// executes behaviors in DI registration order (see the <c>Themia.Mediator</c> pipeline
    /// composition), so register it ahead of validation and the handler. Idempotent: calling it more
    /// than once registers the behavior only once. Requires <c>AddThemiaMultiTenancy</c>, which provides
    /// the <c>ITenantAccessor</c> and <c>IHttpContextAccessor</c> the behavior depends on.
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

        // TryAddEnumerable dedupes by (service, implementation, lifetime) so a double call doesn't
        // register the guard twice (which would run it twice in the pipeline).
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped(typeof(IPipelineBehavior<,>), typeof(TenantGuardBehavior<,>)));
        return services;
    }
}
