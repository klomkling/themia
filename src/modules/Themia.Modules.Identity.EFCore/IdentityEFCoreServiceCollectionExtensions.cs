using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.DependencyInjection;

namespace Themia.Modules.Identity.EFCore.DependencyInjection;

/// <summary>Registers Themia Identity on the EF Core data peer.</summary>
public static class IdentityEFCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Identity services for an EF Core data peer, and a startup check that the registered
    /// context actually maps the Identity entities. Use this instead of <c>AddThemiaIdentityCore</c> when
    /// your peer is EF Core.
    /// </summary>
    /// <remarks>
    /// EF Core needs no registration-time mapping contribution — the entity configuration is applied to
    /// your <c>DbContext</c> instead, by calling
    /// <see cref="EntityConfiguration.ModelBuilderExtensions.ApplyThemiaIdentity"/> from
    /// <c>OnModelCreating</c>. Nothing at registration time can observe whether that call was made, so this
    /// method registers <see cref="IdentityModelValidation"/> as a hosted service: an adopter who takes the
    /// EF package and forgets the model configuration fails at startup with that sentence, instead of
    /// succeeding into a first user operation that queries a table EF Core has never heard of.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityEFCore(
        this IServiceCollection services, Action<IdentityModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddThemiaIdentityCore(configure);
        services.AddHostedService<IdentityEFCoreModelValidator>();
        return services;
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

        services.AddThemiaIdentityCore(options);
        services.AddHostedService<IdentityEFCoreModelValidator>();
        return services;
    }
}
