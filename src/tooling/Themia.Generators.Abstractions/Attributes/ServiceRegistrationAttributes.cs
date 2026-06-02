#nullable enable
using System;

namespace Themia.DependencyInjection;

/// <summary>
/// Marks an assembly to enable automatic service registration via source generation.
/// </summary>
/// <remarks>
/// Apply this attribute at the assembly level to enable compile-time scanning for classes
/// decorated with lifetime attributes ([Singleton], [Scoped], [Transient]).
/// The source generator will discover these classes and generate reflection-free DI registration code.
/// <para>
/// Services are registered based on their implemented interfaces:
/// <list type="bullet">
/// <item><description>Classes with one interface: Registered as interface → implementation</description></item>
/// <item><description>Classes with multiple interfaces: All interfaces registered</description></item>
/// <item><description>Classes without interfaces: Registered as concrete type</description></item>
/// </list>
/// </para>
/// <example>
/// <code>
/// [assembly: GenerateServiceRegistrations]
///
/// namespace MyApp.Services;
///
/// // Registers as IUserService → UserService
/// [Scoped]
/// public class UserService : IUserService
/// {
///     // Implementation
/// }
///
/// // Registers as CacheService (concrete)
/// [Singleton]
/// public class CacheService
/// {
///     // Implementation
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class GenerateServiceRegistrationsAttribute : Attribute
{
}

/// <summary>
/// Explicitly specifies which interface(s) to use when registering a service in DI.
/// </summary>
/// <remarks>
/// Use this attribute when you want explicit control over which interfaces are registered.
/// This is useful when a class implements multiple interfaces but you only want to register specific ones.
/// <para>
/// This attribute can be applied multiple times to register multiple interfaces.
/// </para>
/// <example>
/// <code>
/// // Only register IUserService, not IAuditable
/// [Scoped]
/// [RegisterAs(typeof(IUserService))]
/// public class UserService : IUserService, IAuditable
/// {
///     // Implementation
/// }
///
/// // Register multiple specific interfaces
/// [Scoped]
/// [RegisterAs(typeof(IEmailService))]
/// [RegisterAs(typeof(INotificationService))]
/// public class EmailService : IEmailService, INotificationService, IDisposable
/// {
///     // IDisposable won't be registered
/// }
/// </code>
/// </example>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RegisterAsAttribute : Attribute
{
    /// <summary>
    /// Gets the service type (interface) to register.
    /// </summary>
    public Type ServiceType { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="RegisterAsAttribute"/> class.
    /// </summary>
    /// <param name="serviceType">The interface type to register this service as.</param>
    public RegisterAsAttribute(Type serviceType)
    {
        ServiceType = serviceType;
    }
}
