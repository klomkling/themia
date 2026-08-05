using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Principal;
using Themia.Modules.Identity.Services;

namespace Themia.Modules.Identity.DependencyInjection;

/// <summary>Registers Themia Identity services and authorization integration.</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>Registers the Identity stores, services, password hasher, and options.</summary>
    /// <remarks>
    /// <b>Engine-agnostic: this registers nothing that knows about Dapper or EF Core.</b> Your data peer's
    /// wiring comes from the matching engine package — <c>AddThemiaIdentityDapper</c> in
    /// <c>Themia.Modules.Identity.Dapper</c>, or <c>AddThemiaIdentityEFCore</c> in
    /// <c>Themia.Modules.Identity.EFCore</c> — and each of those calls this method for you. Call this
    /// directly only if you are supplying your own <c>IRepository</c> implementations.
    /// </remarks>
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

    /// <summary>Registers the Identity stores, services, password hasher, and the supplied options instance.</summary>
    /// <remarks>Engine-agnostic — see the other overload.</remarks>
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
        // Services here depend on ILogger<T>; ensure logging is resolvable even on a bare
        // ServiceCollection (no generic host). AddLogging is idempotent/TryAdd-based.
        services.AddLogging();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher, Argon2idPasswordHasher>();

        // TryAdd so an adopter can register their own BEFORE this call and keep it. The default only
        // strips formatting — it deliberately does not decide that 08… and +668… are the same number,
        // because that is only true given a region the framework cannot know. See the interface.
        services.TryAddSingleton<IPhoneNumberNormalizer, FormattingOnlyPhoneNumberNormalizer>();

        services.TryAddScoped<IUserService, UserService>();
        services.TryAddScoped<IRoleService, RoleService>();
        services.TryAddScoped<IClaimService, ClaimService>();
        services.TryAddScoped<IUserTokenService, UserTokenService>();
        services.TryAddScoped<IRefreshTokenService, RefreshTokenService>();
        services.TryAddScoped<IExternalLoginService, ExternalLoginService>();
        services.TryAddScoped<IClaimsPrincipalFactory, ClaimsPrincipalFactory>();

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
}
