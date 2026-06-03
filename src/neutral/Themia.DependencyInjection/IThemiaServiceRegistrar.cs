using Microsoft.Extensions.DependencyInjection;

namespace Themia.DependencyInjection;

/// <summary>
/// Runtime extension hook for arbitrary DI registration logic.
/// Implementations are discovered and instantiated by the Themia source
/// generator; their <see cref="Register"/> method is called from the generated
/// <c>AddThemiaServices</c> method during application startup.
/// </summary>
/// <remarks>
/// Implementing types must be concrete and have a public parameterless
/// constructor. Use this for keyed services, decorators, conditional
/// registration, multi-impl registration, or any logic that doesn't fit a
/// single <c>services.Add{Lifetime}&lt;TService, TImpl&gt;()</c> shape.
/// </remarks>
public interface IThemiaServiceRegistrar
{
    /// <summary>Registers services into the supplied collection.</summary>
    void Register(IServiceCollection services);
}
