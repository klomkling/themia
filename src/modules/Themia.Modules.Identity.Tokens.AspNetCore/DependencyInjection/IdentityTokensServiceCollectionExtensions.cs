using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Themia.Modules.Identity.Tokens.AspNetCore.Signing;
using Themia.Modules.Identity.Tokens.AspNetCore.Tokens;

namespace Themia.Modules.Identity.Tokens.AspNetCore.DependencyInjection;

/// <summary>Registers Themia's persistence-free JWT access-token issuance: validated <see cref="JwtOptions"/>,
/// the symmetric signing-credentials provider, and the default <see cref="IAccessTokenService"/>. Required by
/// the external-auth flow and the bundled Identity ASP.NET Core wiring; standalone-usable for
/// bring-your-own-user-store consumers.</summary>
public static class IdentityTokensServiceCollectionExtensions
{
    /// <summary>Adds JWT access-token issuance. All registrations use <c>TryAdd</c> so an adopter can replace
    /// any piece (e.g. a custom <see cref="IAccessTokenService"/> or signing provider) by registering it first.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the JWT options; validated immediately so misconfiguration fails fast.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityTokens(
        this IServiceCollection services, Action<JwtOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new JwtOptions();
        configure(options);
        options.Validate();
        services.TryAddSingleton(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJwtSigningCredentialsProvider, SymmetricSigningCredentialsProvider>();
        services.TryAddSingleton<IAccessTokenService, AccessTokenService>();
        return services;
    }
}
