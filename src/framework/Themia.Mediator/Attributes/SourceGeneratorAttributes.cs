namespace Themia.Mediator;

/// <summary>
/// Marks an assembly to enable automatic Mediator handler discovery and registration via source generation.
/// </summary>
/// <remarks>
/// Apply this attribute at the assembly level to enable compile-time scanning for handler implementations.
/// The source generator will discover all classes implementing IRequestHandler, ICommandHandler, or IQueryHandler
/// and generate reflection-free registration code.
/// <para>
/// Handlers can optionally be decorated with lifetime attributes:
/// <list type="bullet">
/// <item><description><see cref="SingletonHandlerAttribute"/> - Single instance for application lifetime</description></item>
/// <item><description><see cref="TransientHandlerAttribute"/> - New instance each time</description></item>
/// <item><description>Default - New instance per scope (Scoped)</description></item>
/// </list>
/// </para>
/// <example>
/// <code>
/// [assembly: GenerateMediatorHandlers]
///
/// // Default Scoped lifetime
/// public class CreateOrderHandler : ICommandHandler&lt;CreateOrder, Guid&gt;
/// {
///     // Implementation
/// }
///
/// // Explicit Singleton lifetime
/// [SingletonHandler]
/// public class CacheHandler : IQueryHandler&lt;GetCache, CachedData&gt;
/// {
///     // Implementation
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class GenerateMediatorHandlersAttribute : Attribute
{
}

/// <summary>
/// Marks a mediator handler class to be registered with singleton lifetime.
/// The handler instance will be shared across all requests for the application lifetime.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SingletonHandlerAttribute : Attribute
{
}

/// <summary>
/// Marks a mediator handler class to be registered with transient lifetime.
/// A new handler instance will be created for each request.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class TransientHandlerAttribute : Attribute
{
}
