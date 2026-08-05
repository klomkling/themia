using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.DependencyInjection;
using Themia.Modules.Identity.Mapping;

namespace Themia.Modules.Identity.Dapper.DependencyInjection;

/// <summary>Registers Themia Identity on the Dapper data peer.</summary>
public static class IdentityDapperServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Identity services and contributes the Identity entity mappings to the Dapper
    /// <see cref="EntityMappingRegistry"/>. Use this instead of <c>AddThemiaIdentityServices</c> when your
    /// data peer is Dapper.
    /// </summary>
    /// <remarks>
    /// <b>Call this AFTER your Dapper peer registration</b> (<c>AddThemiaDapperPostgres</c> or a sibling),
    /// because the registry it contributes to is created there.
    /// <para>
    /// The core used to do this itself, by scanning the service collection for an already-registered
    /// registry — so the Dapper path was inferred rather than chosen, and inferred wrong, silently,
    /// whenever the two registrations ran in the other order: no error, no log, just identity mappings
    /// never applied until a query came back with the wrong columns. This method cannot be satisfied by an
    /// ordering accident. If the registry is missing it says so.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">
    /// No Dapper <see cref="EntityMappingRegistry"/> is registered — the peer registration has not run yet.
    /// </exception>
    public static IServiceCollection AddThemiaIdentityDapper(
        this IServiceCollection services, Action<IdentityModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddThemiaIdentityServices(configure);
        ContributeMappings(services);
        return services;
    }

    /// <summary>
    /// Same as the public overload but taking a pre-built options instance. Internal rather than public so
    /// the options-instance form does not become a second public overload — the analyzer requires the
    /// overload carrying optional parameters to have the most parameters, and adopters have the lambda.
    /// </summary>
    internal static IServiceCollection AddThemiaIdentityDapper(this IServiceCollection services, IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddThemiaIdentityServices(options);
        ContributeMappings(services);
        return services;
    }

    private static void ContributeMappings(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(EntityMappingRegistry)
                && services[i].ImplementationInstance is EntityMappingRegistry registry)
            {
                IdentityDapperMappings.Apply(registry);
                return;
            }
        }

        // Loud, because the alternative is the failure this package exists to remove: mappings silently
        // not applied, and a query returning the wrong columns much later with nothing to connect it to.
        throw new InvalidOperationException(
            "AddThemiaIdentityDapper found no Dapper EntityMappingRegistry. Register the Dapper data peer "
            + "first (e.g. services.AddThemiaDapperPostgres(configuration)), then call this.");
    }
}
