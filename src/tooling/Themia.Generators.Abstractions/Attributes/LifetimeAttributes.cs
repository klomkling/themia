#nullable enable
using System;

namespace Themia.DependencyInjection;

/// <summary>
/// Specifies that a service or handler should be registered with Singleton lifetime.
/// </summary>
/// <remarks>
/// A singleton service is created once and shared across the entire application lifetime.
/// Use for stateless services or services that maintain application-wide state.
/// <para>
/// When applied to handlers in source-generated scenarios (e.g., Mediator handlers),
/// the source generator will register the handler with <c>Singleton</c> lifetime.
/// </para>
/// <example>
/// <code>
/// [Singleton]
/// public class CacheHandler : ICommandHandler&lt;RefreshCache, Result&gt;
/// {
///     private readonly IMemoryCache _cache;
///
///     public CacheHandler(IMemoryCache cache)
///     {
///         _cache = cache;
///     }
///
///     public Task&lt;Result&gt; HandleAsync(RefreshCache request, CancellationToken ct)
///     {
///         // Implementation
///     }
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SingletonAttribute : Attribute
{
}

/// <summary>
/// Specifies that a service or handler should be registered with Scoped lifetime.
/// </summary>
/// <remarks>
/// A scoped service is created once per scope (typically per HTTP request in web applications).
/// This is the default lifetime for most handlers and services.
/// <para>
/// When applied to handlers in source-generated scenarios (e.g., Mediator handlers),
/// the source generator will register the handler with <c>Scoped</c> lifetime.
/// </para>
/// <para>
/// This attribute is optional as Scoped is the default lifetime when no attribute is specified.
/// </para>
/// <example>
/// <code>
/// // Explicit (optional)
/// [Scoped]
/// public class CreateOrderHandler : ICommandHandler&lt;CreateOrder, Guid&gt;
/// {
///     private readonly DbContext _db;
///
///     public CreateOrderHandler(DbContext db)
///     {
///         _db = db;
///     }
///
///     public async Task&lt;Guid&gt; HandleAsync(CreateOrder request, CancellationToken ct)
///     {
///         var order = new Order { /* ... */ };
///         _db.Orders.Add(order);
///         await _db.SaveChangesAsync(ct);
///         return order.Id;
///     }
/// }
///
/// // Implicit (no attribute = Scoped by default)
/// public class GetOrderHandler : IQueryHandler&lt;GetOrder, Order&gt;
/// {
///     // Also registered as Scoped
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ScopedAttribute : Attribute
{
}

/// <summary>
/// Specifies that a service or handler should be registered with Transient lifetime.
/// </summary>
/// <remarks>
/// A transient service is created each time it is requested from the service container.
/// Use for lightweight, stateless services where instance creation is cheap.
/// <para>
/// When applied to handlers in source-generated scenarios (e.g., Mediator handlers),
/// the source generator will register the handler with <c>Transient</c> lifetime.
/// </para>
/// <example>
/// <code>
/// [Transient]
/// public class ValidateOrderHandler : ICommandHandler&lt;ValidateOrder, ValidationResult&gt;
/// {
///     public Task&lt;ValidationResult&gt; HandleAsync(ValidateOrder request, CancellationToken ct)
///     {
///         var isValid = request.Quantity &gt; 0 &amp;&amp; !string.IsNullOrEmpty(request.Product);
///         return Task.FromResult(new ValidationResult { IsValid = isValid });
///     }
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TransientAttribute : Attribute
{
}
