using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.Connection;

namespace Themia.Framework.Data.Dapper.Mapping;

/// <summary>
/// Contributes a module's entity mappings to the Dapper <see cref="EntityMappingRegistry"/> that the data
/// peer registration created, without building a service provider.
/// </summary>
/// <remarks>
/// One implementation for every module. Four modules had hand-rolled the same service-collection scan and
/// the copies had drifted into three different behaviours for one adopter mistake — Storage and
/// Notifications no-opped silently, Messaging threw only when a Dapper peer was already present, Identity
/// always threw — so registering the peer after the modules produced a hard failure from one module and
/// silently unmapped tables from the others in the same startup.
/// <para>
/// Two entry points, because the two situations are genuinely different:
/// <see cref="ContributeDapperMappings"/> for a module that supports both peers (no registry and no Dapper
/// peer means a legitimate EF Core adopter), and <see cref="RequireDapperMappings"/> for a Dapper-only
/// package, where no registry can only mean the wrong order.
/// </para>
/// </remarks>
public static class DapperMappingRegistration
{
    /// <summary>
    /// Returns the <see cref="EntityMappingRegistry"/> singleton instance already registered in
    /// <paramref name="services"/>, or <see langword="null"/> when none is.
    /// </summary>
    /// <param name="services">The service collection to scan.</param>
    /// <returns>The registered registry instance, or <see langword="null"/>.</returns>
    public static EntityMappingRegistry? FindEntityMappingRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Last registration wins, matching how Microsoft DI resolves a duplicated service type.
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(EntityMappingRegistry)
                && services[i].ImplementationInstance is EntityMappingRegistry registry)
            {
                return registry;
            }
        }

        return null;
    }

    /// <summary>
    /// Contributes the mappings for a module that supports both data peers. Applies them when a registry is
    /// present, throws when a Dapper peer is present without one, and does nothing when neither is — the
    /// signature of a genuine EF Core adopter.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="apply">Applies the module's mappings to the registry.</param>
    /// <param name="callerName">The registration method's name, used in the error message.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">
    /// A Dapper peer is registered but its <see cref="EntityMappingRegistry"/> is not, which can only mean
    /// this method ran before the peer registration.
    /// </exception>
    public static IServiceCollection ContributeDapperMappings(
        this IServiceCollection services, Action<EntityMappingRegistry> apply, string callerName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerName);

        var registry = services.FindEntityMappingRegistry();
        if (registry is not null)
        {
            apply(registry);
            return services;
        }

        if (services.Any(d => d.ServiceType == typeof(IDapperConnectionContext)))
        {
            throw new InvalidOperationException(OrderingMessage(callerName));
        }

        // Neither a registry nor a Dapper connection context: an EF Core peer, which has no registry to
        // contribute to. Nothing to do, and nothing wrong.
        return services;
    }

    /// <summary>
    /// Contributes the mappings for a Dapper-only package. Throws when no registry is registered, because
    /// for such a package that can only mean the peer registration has not run yet.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="apply">Applies the package's mappings to the registry.</param>
    /// <param name="callerName">The registration method's name, used in the error message.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">No <see cref="EntityMappingRegistry"/> is registered.</exception>
    public static IServiceCollection RequireDapperMappings(
        this IServiceCollection services, Action<EntityMappingRegistry> apply, string callerName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentException.ThrowIfNullOrWhiteSpace(callerName);

        var registry = services.FindEntityMappingRegistry()
            ?? throw new InvalidOperationException(OrderingMessage(callerName));

        apply(registry);
        return services;
    }

    // Loud, because the alternative is the failure this helper exists to remove: mappings silently not
    // applied, and a query returning against an unqualified table name much later with nothing to connect
    // it to the registration order.
    private static string OrderingMessage(string callerName) =>
        $"{callerName} found no Dapper EntityMappingRegistry to contribute its entity mappings to. "
        + "Register the Dapper data peer FIRST — AddThemiaDapperPostgres/AddThemiaDapperMySql/"
        + $"AddThemiaDapperSqlServer(configuration), or AddThemiaDapperCore() — then call {callerName}. "
        + "This also applies when the call comes from a Themia module's ConfigureServices: the module must "
        + "be configured after the peer registration, not before it.";
}
