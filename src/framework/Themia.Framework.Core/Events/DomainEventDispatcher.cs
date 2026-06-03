using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Events;

namespace Themia.Framework.Core.Events;

/// <summary>
/// Default implementation of domain event dispatcher using service provider to resolve handlers.
/// Uses compiled expressions for better performance than reflection.
/// </summary>
public sealed class DomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IServiceProvider serviceProvider;
    private static readonly ConcurrentDictionary<Type, Func<object, IDomainEvent, CancellationToken, Task>> HandlerInvokers = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="DomainEventDispatcher"/> class.
    /// </summary>
    /// <param name="serviceProvider">Service provider for resolving event handlers.</param>
    public DomainEventDispatcher(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        this.serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public async Task DispatchAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        var eventType = domainEvent.GetType();
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);

        using var scope = serviceProvider.CreateScope();
        var handlers = scope.ServiceProvider.GetServices(handlerType);

        var invoker = HandlerInvokers.GetOrAdd(eventType, CreateHandlerInvoker);

        var tasks = handlers
            .Where(handler => handler is not null)
            .Select(handler => invoker(handler!, domainEvent, cancellationToken));

        await Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (var domainEvent in domainEvents)
        {
            await DispatchAsync(domainEvent, cancellationToken);
        }
    }

    /// <summary>
    /// Creates a compiled expression for invoking a handler's HandleAsync method.
    /// This avoids reflection overhead on every invocation.
    /// </summary>
    private static Func<object, IDomainEvent, CancellationToken, Task> CreateHandlerInvoker(Type eventType)
    {
        var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(eventType);
        var handleMethod = handlerType.GetMethod(nameof(IDomainEventHandler<IDomainEvent>.HandleAsync))!;

        // Parameters: (object handler, IDomainEvent domainEvent, CancellationToken cancellationToken)
        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var eventParam = Expression.Parameter(typeof(IDomainEvent), "domainEvent");
        var tokenParam = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

        // Cast handler to the specific handler type
        var typedHandler = Expression.Convert(handlerParam, handlerType);

        // Cast event to the specific event type
        var typedEvent = Expression.Convert(eventParam, eventType);

        // Call handler.HandleAsync(typedEvent, cancellationToken)
        var methodCall = Expression.Call(typedHandler, handleMethod, typedEvent, tokenParam);

        // Compile to a delegate
        var lambda = Expression.Lambda<Func<object, IDomainEvent, CancellationToken, Task>>(
            methodCall,
            handlerParam,
            eventParam,
            tokenParam);

        return lambda.Compile();
    }
}
