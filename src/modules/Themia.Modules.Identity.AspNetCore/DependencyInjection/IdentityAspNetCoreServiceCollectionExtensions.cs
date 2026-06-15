using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Themia.Modules.Identity.AspNetCore.Options;
using Themia.Modules.Identity.AspNetCore.Signing;
using Themia.Modules.Identity.AspNetCore.Tokens;

namespace Themia.Modules.Identity.AspNetCore.DependencyInjection;

/// <summary>Registers the Themia Identity JWT slice. Requires <c>AddThemiaIdentityServices()</c> and
/// <c>AddThemiaIdentityAuthorization()</c> to have run (for <c>IUserService</c>,
/// <c>IRefreshTokenService</c>, <c>IClaimsPrincipalFactory</c>, <c>ICurrentUser</c>).</summary>
public static class IdentityAspNetCoreServiceCollectionExtensions
{
    /// <summary>Validates <see cref="JwtOptions"/> and registers token services, the signing provider,
    /// the authentication flow, and the default no-op hooks (all via <c>TryAdd</c> so adopters can
    /// replace any piece).</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the JWT options.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityAspNetCore(
        this IServiceCollection services,
        Action<JwtOptions> configure)
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
        services.TryAddScoped<IAuthenticationFlow, AuthenticationFlow>();
        services.TryAddScoped<IAuthenticationHooks, AuthenticationHooksBase>();

        return services;
    }

    /// <summary>Adds the JwtBearer validation scheme wired to <see cref="JwtOptions"/> and the registered
    /// <see cref="IJwtSigningCredentialsProvider"/>. Call after <c>AddAuthentication(...)</c>.</summary>
    /// <param name="builder">The authentication builder.</param>
    /// <param name="scheme">The scheme name; defaults to <see cref="JwtBearerDefaults.AuthenticationScheme"/>.</param>
    /// <returns>The same authentication builder.</returns>
    public static AuthenticationBuilder AddThemiaJwtBearer(
        this AuthenticationBuilder builder,
        string? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var schemeName = scheme ?? JwtBearerDefaults.AuthenticationScheme;

        builder.AddJwtBearer(schemeName, _ => { });
        builder.Services.AddOptions<JwtBearerOptions>(schemeName)
            .Configure<IJwtSigningCredentialsProvider, JwtOptions>((bearer, signing, jwt) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signing.ValidationKey,
                    ValidateLifetime = true,
                    ClockSkew = jwt.ClockSkew,
                    NameClaimType = ClaimTypes.Name,
                    RoleClaimType = ClaimTypes.Role,
                };
            });

        return builder;
    }
}
