using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Mapping;
using Themia.Modules.Identity.Principal;
using Themia.Modules.Identity.Services;

namespace Themia.Modules.Identity.DependencyInjection;

/// <summary>Registers Themia Identity services and authorization integration.</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>Registers the Identity stores, services, password hasher, and options. If a Dapper <see cref="EntityMappingRegistry"/> is already registered, contributes the Identity mappings to it.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityServices(this IServiceCollection services, Action<IdentityModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new IdentityModuleOptions();
        configure?.Invoke(options);
        options.Validate();
        services.TryAddSingleton(options);

        return AddThemiaIdentityServicesCore(services);
    }

    /// <summary>Registers the Identity stores, services, password hasher, and the supplied options instance. If a Dapper <see cref="EntityMappingRegistry"/> is already registered, contributes the Identity mappings to it.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">The validated module options to register.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityServices(this IServiceCollection services, IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        options.Validate();
        services.TryAddSingleton(options);

        return AddThemiaIdentityServicesCore(services);
    }

    private static IServiceCollection AddThemiaIdentityServicesCore(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher, Argon2idPasswordHasher>();

        services.TryAddScoped<IUserService, UserService>();
        services.TryAddScoped<IRoleService, RoleService>();
        services.TryAddScoped<IClaimService, ClaimService>();
        services.TryAddScoped<IUserTokenService, UserTokenService>();
        services.TryAddScoped<IRefreshTokenService, RefreshTokenService>();
        services.TryAddScoped<IClaimsPrincipalFactory, ClaimsPrincipalFactory>();

        // Dapper adopters: contribute mappings to the registry they already registered.
        ContributeDapperMappings(services);

        return services;
    }

    /// <summary>Registers the current-user principal and replaces the framework's null audit-user accessor.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.TryAddScoped<ICurrentUser, CurrentUser>();

        // Override the framework's NullCurrentUserAccessor so audit columns capture the real user.
        services.RemoveAll<ICurrentUserAccessor>();
        services.AddScoped<ICurrentUserAccessor, IdentityCurrentUserAccessor>();
        return services;
    }

    private static void ContributeDapperMappings(IServiceCollection services)
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
    }
}
