using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Themia.Caching;

/// <summary>
/// Defines a contract for registering custom cache providers.
/// </summary>
public interface ICacheProviderRegistration
{
    /// <summary>
    /// Registers a cache provider with the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration.</param>
    void Register(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Gets the priority of this provider registration (higher priority is preferred).
    /// </summary>
    int Priority { get; }
}
