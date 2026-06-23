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
using Themia.Modules.Identity.ExternalAuth.AspNetCore.External;
using Themia.Modules.Identity.Tokens.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Themia.Modules.Identity.Tokens.AspNetCore.Signing;
using Themia.Modules.Identity.Tokens.AspNetCore.Tokens;

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
            || !IsRegistered(services, typeof(IExternalLoginService))
            || !IsRegistered(services, typeof(IClaimsPrincipalFactory)))
        {
            throw new InvalidOperationException(
                "AddThemiaIdentityAspNetCore requires AddThemiaIdentityServices() to be called first " +
                "(IUserService, IRefreshTokenService, IExternalLoginService, and IClaimsPrincipalFactory " +
                "must be registered).");
        }

        // Validate JwtOptions and register the JWT access-token issuance stack (JwtOptions, TimeProvider,
        // signing provider, IAccessTokenService) via the persistence-free Tokens package.
        services.AddThemiaIdentityTokens(configure);

        // The authentication flow depends on ILogger<T>; ensure logging is resolvable even on a
        // bare ServiceCollection. AddLogging is idempotent/TryAdd-based.
        services.AddLogging();

        services.TryAddScoped<IAuthenticationFlow, AuthenticationFlow>();
        services.TryAddScoped<IAuthenticationHooks, AuthenticationHooksBase>();

        // External-login flow + default no-op hooks. The flow additionally needs IExternalLoginService
        // (from AddThemiaIdentityServices) and IExternalAuthProviderRegistry (from AddThemiaExternalAuth);
        // both are resolved at runtime, so the registration order of those calls does not matter. The flow
        // is inert unless the host also maps MapIdentityExternalAuthEndpoints and registers a provider.
        services.TryAddScoped<IExternalAuthenticationFlow, ExternalAuthenticationFlow>();
        services.TryAddScoped<IExternalAuthenticationHooks, ExternalAuthenticationHooksBase>();

        return services;
    }

    /// <summary>Adds the JwtBearer validation scheme wired to <see cref="JwtOptions"/> and the registered
    /// <see cref="IJwtSigningCredentialsProvider"/>. Call after <c>AddAuthentication(...)</c>.</summary>
    /// <remarks>The internal <see cref="ClaimTypes"/> principal shape that <c>ICurrentUser</c>, the audit
    /// accessor, and <c>[Authorize(Roles)]</c> depend on is established by THIS scheme's
    /// <c>OnTokenValidated</c> remap — it is not carried by the token. The minted access token only
    /// carries the standard short <c>sub</c>/<c>name</c>/<c>role</c> claims on the wire, so a consumer that
    /// validates a Themia access token WITHOUT this configured JwtBearer scheme (e.g. a bare
    /// <c>JsonWebTokenHandler.ValidateToken</c> in a background or non-HTTP context) sees only the short
    /// claims and must map them to the long <see cref="ClaimTypes"/> URIs itself.</remarks>
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

    /// <summary>Re-adds the long <see cref="ClaimTypes"/> claims from their short JWT counterparts,
    /// driven by <see cref="JwtClaimNames.WellKnown"/>. Idempotent via <see cref="ClaimsIdentity.HasClaim(string,string)"/>.
    /// Collects matches in a single enumeration and adds afterward so the identity's claim collection is
    /// never mutated mid-enumeration; at most one small list is allocated, and only when there is
    /// something to add.</summary>
    private static void AddLongClaims(TokenValidatedContext context)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        List<Claim>? toAdd = null;
        foreach (var claim in identity.Claims)
        {
            foreach (var (longType, shortType) in JwtClaimNames.WellKnown)
            {
                if (claim.Type == shortType && !identity.HasClaim(longType, claim.Value))
                {
                    (toAdd ??= []).Add(new Claim(longType, claim.Value, claim.ValueType, claim.Issuer));
                }
            }
        }

        if (toAdd is null)
        {
            return;
        }

        foreach (var claim in toAdd)
        {
            identity.AddClaim(claim);
        }
    }
}
