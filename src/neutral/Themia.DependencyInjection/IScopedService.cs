namespace Themia.DependencyInjection;

/// <summary>
/// Marker interface — implementing this registers the class as
/// <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped"/>
/// when the Themia source generator runs.
/// </summary>
public interface IScopedService;

/// <summary>
/// Generic marker — pins the registration's service type to <typeparamref name="TService"/>.
/// </summary>
public interface IScopedService<TService> : IScopedService;
