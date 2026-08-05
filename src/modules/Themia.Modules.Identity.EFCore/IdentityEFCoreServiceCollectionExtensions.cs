using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.DependencyInjection;

namespace Themia.Modules.Identity.EFCore.DependencyInjection;

/// <summary>Registers Themia Identity on the EF Core data peer.</summary>
public static class IdentityEFCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Identity services for an EF Core data peer. Use this instead of
    /// <c>AddThemiaIdentityServices</c> when your peer is EF Core.
    /// </summary>
    /// <remarks>
    /// EF Core needs no registration-time mapping contribution — the entity configuration is applied to
    /// your <c>DbContext</c> instead, by calling
    /// <see cref="EntityConfiguration.ModelBuilderExtensions.ApplyThemiaIdentity"/> from
    /// <c>OnModelCreating</c>. This method exists so the engine is named at the call site on both peers
    /// rather than only on one, and so an adopter who takes the EF package but forgets the model
    /// configuration has one obvious place to look.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityEFCore(
        this IServiceCollection services, Action<IdentityModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddThemiaIdentityServices(configure);
    }

    /// <summary>
    /// Same as the public overload but taking a pre-built options instance. Internal rather than public so
    /// the options-instance form does not become a second public overload — the analyzer requires the
    /// overload carrying optional parameters to have the most parameters, and adopters have the lambda.
    /// </summary>
    internal static IServiceCollection AddThemiaIdentityEFCore(this IServiceCollection services, IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddThemiaIdentityServices(options);
        return services;
    }
}
