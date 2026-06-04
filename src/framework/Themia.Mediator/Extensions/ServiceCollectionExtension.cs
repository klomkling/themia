using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Caching;
using Themia.Caching.Extensions;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Behaviors;
using Themia.Mediator.Configuration;
using Themia.Mediator.Infrastructure;

namespace Themia.Mediator.Extensions;

/// <summary>
/// Extension methods for configuring Themia Mediator services.
/// </summary>
public static class ServiceCollectionExtension
{
    /// <summary>
    /// Registers Themia Mediator pipeline behaviors.
    /// </summary>
    /// <param name="services">The service collection to add services to.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// This method registers pipeline behaviors (Validation, Logging, Caching, Performance, Transaction).
    ///
    /// IMPORTANT: You must also:
    /// <list type="number">
    /// <item><description>Add <c>[assembly: GenerateMediatorHandlers]</c> to your assembly.</description></item>
    /// <item><description>Call <c>services.AddGeneratedMediatorHandlers()</c> to register handlers and the dispatcher.</description></item>
    /// </list>
    ///
    /// Example:
    /// <code>
    /// services.AddApplicationMediator();       // Registers behaviors
    /// services.AddGeneratedMediatorHandlers(); // Registers dispatcher and handlers (source-generated)
    /// </code>
    /// </remarks>
    public static IServiceCollection AddApplicationMediator(this IServiceCollection services)
    {
        // Note: The IMediator (MediatorDispatcher) is registered via AddGeneratedMediatorHandlers(),
        // which is source-generated when [assembly: GenerateMediatorHandlers] is present.

        // Register caching infrastructure
        services.AddScoped<ICacheKeyFactory, DefaultCacheKeyFactory>();
        services.AddSingleton<ICacheMetadataProvider, AttributeCacheMetadataProvider>();
        services.TryAddSingleton<IDistributedLockProvider, InMemoryDistributedLockProvider>();
        services.AddScoped<ICacheKeyIndex, CacheKeyIndex>();
        services.Configure<MediatorCachingOptions>(_ => { });

        // Register a default cache provider if the application hasn't configured one yet.
        if (!services.Any(sd => sd.ServiceType == typeof(IThemiaCacheProvider)))
        {
            services.AddThemiaCaching();
        }

        // Register pipeline behaviors in order:
        // 1. ValidationBehavior
        // 2. LoggingBehavior
        // 3. CachingBehavior
        // 4. PerformanceBehavior
        // 5. TransactionBehavior
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(PerformanceBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        return services;
    }
}
