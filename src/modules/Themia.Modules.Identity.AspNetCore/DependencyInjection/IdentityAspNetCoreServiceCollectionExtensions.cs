using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Abstractions;
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

        // Fail fast at startup if the prerequisite core registrations are missing, instead of
        // surfacing a runtime 500 on the first /auth/login. The flow constructor needs these.
        static bool IsRegistered(IServiceCollection s, Type t) => s.Any(d => d.ServiceType == t);
        if (!IsRegistered(services, typeof(IUserService))
            || !IsRegistered(services, typeof(IRefreshTokenService))
            || !IsRegistered(services, typeof(IClaimsPrincipalFactory)))
        {
            throw new InvalidOperationException(
                "AddThemiaIdentityAspNetCore requires AddThemiaIdentityServices() to be called first " +
                "(IUserService, IRefreshTokenService, and IClaimsPrincipalFactory must be registered).");
        }

        var options = new JwtOptions();
        configure(options);
        options.Validate();
        services.TryAddSingleton(options);

        // The authentication flow depends on ILogger<T>; ensure logging is resolvable even on a
        // bare ServiceCollection. AddLogging is idempotent/TryAdd-based.
        services.AddLogging();

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

                // Tokens carry standard short claim names (sub/name/role) on the wire. After
                // validation, re-add the long ClaimTypes.* claims so the bearer principal matches the
                // cookie principal shape and ICurrentUser/[Authorize(Roles)]/the audit accessor work
                // unchanged. The namespaced Themia claims (themia:tenant_id, themia:is_platform, …)
                // pass through verbatim and need no remap. Chain onto any existing handler instead of
                // clobbering it.
                bearer.Events ??= new JwtBearerEvents();
                var inner = bearer.Events.OnTokenValidated;
                bearer.Events.OnTokenValidated = async context =>
                {
                    AddLongClaims(context);
                    if (inner is not null)
                    {
                        await inner(context).ConfigureAwait(false);
                    }
                };
            });

        return builder;
    }

    private static void AddLongClaims(TokenValidatedContext context)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        Mirror(identity, JwtClaimNames.Subject, ClaimTypes.NameIdentifier);
        Mirror(identity, JwtClaimNames.Name, ClaimTypes.Name);
        Mirror(identity, JwtClaimNames.Role, ClaimTypes.Role);
    }

    /// <summary>Adds a <paramref name="longType"/> claim for each <paramref name="shortType"/> claim,
    /// skipping values already present under the long type (idempotent — guards against double-add).</summary>
    private static void Mirror(ClaimsIdentity identity, string shortType, string longType)
    {
        var existing = identity.FindAll(longType).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var claim in identity.FindAll(shortType).ToList())
        {
            if (existing.Add(claim.Value))
            {
                identity.AddClaim(new Claim(longType, claim.Value, claim.ValueType, claim.Issuer));
            }
        }
    }
}
