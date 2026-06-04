using Microsoft.Extensions.DependencyInjection;

namespace Themia.Services;

/// <summary>
/// Entry point for wiring Themia service defaults. The cross-cutting infrastructure-service
/// contracts in <c>Themia.Services.Abstractions</c> have no default implementation in 0.2.0 — they
/// are forward-seams whose implementations ship with their respective Themia modules (0.4.0+). This
/// extension therefore registers nothing by default; it exists to give consumers and future modules a
/// single, stable place to compose service registrations via the <c>configure</c> callback.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Themia service defaults. No services are registered by default in 0.2.0; pass
    /// <paramref name="configure"/> to add your own implementations of the infrastructure-service
    /// contracts.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional callback to register service implementations.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddThemiaServices(
        this IServiceCollection services,
        Action<ThemiaServicesBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        configure?.Invoke(new ThemiaServicesBuilder(services));

        return services;
    }
}

/// <summary>
/// Provides access to the service collection for registering Themia service implementations when
/// calling <see cref="ServiceCollectionExtensions.AddThemiaServices"/>.
/// </summary>
public sealed class ThemiaServicesBuilder
{
    internal ThemiaServicesBuilder(IServiceCollection services) => Services = services;

    /// <summary>Gets the underlying service collection.</summary>
    public IServiceCollection Services { get; }
}
