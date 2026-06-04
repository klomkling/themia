namespace Themia.Services.Abstractions;

/// <summary>
/// Marker interface for Themia-managed services.
/// </summary>
/// <remarks>
/// This interface serves as a base marker for service discovery and registration.
/// All services should implement one of the specific service interfaces:
/// <list type="bullet">
///   <item><see cref="IDomainService"/> - Business logic services</item>
///   <item><see cref="IInfrastructureService"/> - Infrastructure concerns (email, storage, etc.)</item>
///   <item><see cref="IIntegrationService"/> - External system integrations</item>
/// </list>
/// </remarks>
public interface IService
{
}
