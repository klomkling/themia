namespace Themia.DependencyInjection;

/// <summary>
/// Marker interface — implementing this registers the class as
/// <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>
/// when the Themia source generator runs.
/// </summary>
public interface ISingletonService;

/// <summary>
/// Generic marker — pins the registration's service type to <typeparamref name="TService"/>.
/// </summary>
public interface ISingletonService<TService> : ISingletonService;
