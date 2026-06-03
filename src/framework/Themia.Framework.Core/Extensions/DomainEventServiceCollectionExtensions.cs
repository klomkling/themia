using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Core.Abstractions.Events;
using Themia.Framework.Core.Events;

namespace Themia.Framework.Core.Extensions;

/// <summary>
/// Extension methods for registering domain event services.
/// </summary>
public static class DomainEventServiceCollectionExtensions
{
    /// <summary>
    /// Registers the domain event dispatcher and optionally scans for event handlers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDomainEventDispatcher(this IServiceCollection services)
    {
        services.TryAddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
        return services;
    }

    /// <summary>
    /// Registers a domain event handler.
    /// </summary>
    /// <typeparam name="TEvent">The type of domain event.</typeparam>
    /// <typeparam name="THandler">The type of handler.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="lifetime">The service lifetime (default is Scoped).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDomainEventHandler<TEvent, THandler>(
        this IServiceCollection services,
        ServiceLifetime lifetime = ServiceLifetime.Scoped)
        where TEvent : IDomainEvent
        where THandler : class, IDomainEventHandler<TEvent>
    {
        services.Add(new ServiceDescriptor(
            typeof(IDomainEventHandler<TEvent>),
            typeof(THandler),
            lifetime));

        return services;
    }
}
