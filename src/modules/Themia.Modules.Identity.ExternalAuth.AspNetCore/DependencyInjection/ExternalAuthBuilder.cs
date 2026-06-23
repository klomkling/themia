using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.External;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.Options;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.DependencyInjection;

/// <summary>Registers the Themia Identity external-auth (OAuth/OIDC) providers, the provider registry,
/// and a named <see cref="HttpClient"/> per provider.</summary>
public static class ExternalAuthServiceCollectionExtensions
{
    /// <summary>Begins external-auth registration. Chain <see cref="ExternalAuthBuilder.AddGoogle"/>,
    /// <see cref="ExternalAuthBuilder.AddLine"/>, <see cref="ExternalAuthBuilder.AddOidc"/> (the
    /// escape hatch for any custom OIDC provider), or <c>AddProvider</c> calls onto the result.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The external-auth builder.</returns>
    public static ExternalAuthBuilder AddThemiaExternalAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IExternalAuthProviderRegistry, ExternalAuthProviderRegistry>();

        // The external-login flow + default no-op hooks live here (not in AddThemiaIdentityAspNetCore)
        // so a bring-your-own-user-store consumer gets them without the local/password wiring. The flow
        // resolves IExternalLoginService and the token seams at runtime; call ValidateThemiaExternalAuth
        // once wiring is complete to fail fast if any are missing.
        services.TryAddScoped<IExternalAuthenticationFlow, External.ExternalAuthenticationFlow>();
        services.TryAddScoped<IExternalAuthenticationHooks, External.ExternalAuthenticationHooksBase>();
        return new ExternalAuthBuilder(services);
    }

    /// <summary>Fail-fast check that the external-login flow's runtime dependencies are registered:
    /// <see cref="IExternalLoginService"/> (the bring-your-own user-store seam), and the token seams
    /// <see cref="IAccessTokenService"/> / <see cref="IRefreshTokenService"/> / <see cref="IClaimsPrincipalFactory"/>.
    /// Deliberately does NOT require <c>IUserService</c> — the external flow never uses it.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    /// <exception cref="InvalidOperationException">One or more required services are not registered.</exception>
    public static IServiceCollection ValidateThemiaExternalAuth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        static bool Has(IServiceCollection s, Type t) => s.Any(d => d.ServiceType == t);
        var missing = new List<string>();
        if (!Has(services, typeof(IExternalLoginService))) missing.Add(nameof(IExternalLoginService));
        if (!Has(services, typeof(IAccessTokenService))) missing.Add(nameof(IAccessTokenService));
        if (!Has(services, typeof(IRefreshTokenService))) missing.Add(nameof(IRefreshTokenService));
        if (!Has(services, typeof(IClaimsPrincipalFactory))) missing.Add(nameof(IClaimsPrincipalFactory));
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Themia external auth requires these services to be registered: " + string.Join(", ", missing) +
                ". Register your IExternalLoginService (and IAccessTokenService via AddThemiaIdentityTokens, " +
                "plus IRefreshTokenService / IClaimsPrincipalFactory).");
        }

        return services;
    }
}

/// <summary>A fluent builder for registering external-auth providers. Credentials are validated
/// eagerly (fail-fast) when each provider is added.</summary>
public sealed class ExternalAuthBuilder
{
    private const string GoogleTokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string GoogleMetadataAddress = "https://accounts.google.com/.well-known/openid-configuration";
    private const string GoogleIssuer = "https://accounts.google.com";
    private const string LineTokenEndpoint = "https://api.line.me/oauth2/v2.1/token";
    private const string LineIssuer = "https://access.line.me";

    private readonly IServiceCollection services;

    internal ExternalAuthBuilder(IServiceCollection services) => this.services = services;

    /// <summary>The underlying service collection.</summary>
    public IServiceCollection Services => services;

    /// <summary>Registers Google as an OIDC provider named <c>google</c> (RS256 via Google's OIDC
    /// discovery document, so signing-key rotations are picked up automatically).</summary>
    /// <param name="configure">Configures the Google credentials.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentException">The credentials are blank.</exception>
    public ExternalAuthBuilder AddGoogle(Action<GoogleOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new GoogleOptions();
        configure(options);
        options.Validate();

        return AddOidc(new OidcProviderConfig
        {
            Name = "google",
            TokenEndpoint = new Uri(GoogleTokenEndpoint),
            ClientId = options.ClientId,
            ClientSecret = options.ClientSecret,
            Scopes = options.Scopes,
            Issuer = GoogleIssuer,
            Audience = options.ClientId,
            MetadataAddress = new Uri(GoogleMetadataAddress),
        });
    }

    /// <summary>Registers LINE as an OIDC provider named <c>line</c> (HS256 via the channel secret).
    /// <para>By default a LINE login does <b>not</b> auto-link to an existing local account: LINE emits
    /// no <c>email_verified</c> claim, so its email is treated as unverified — it is neither used to
    /// adopt an existing account nor persisted onto a new user. Opt in via
    /// <see cref="LineOptions.EmailAlwaysVerified"/> only if you trust LINE's email verification for
    /// your channel.</para></summary>
    /// <param name="configure">Configures the LINE credentials.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="ArgumentException">The credentials are blank.</exception>
    public ExternalAuthBuilder AddLine(Action<LineOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new LineOptions();
        configure(options);
        options.Validate();

        return AddOidc(new OidcProviderConfig
        {
            Name = "line",
            TokenEndpoint = new Uri(LineTokenEndpoint),
            ClientId = options.ChannelId,
            ClientSecret = options.ChannelSecret,
            Scopes = options.Scopes,
            Issuer = LineIssuer,
            Audience = options.ChannelId,
            SymmetricSecret = options.ChannelSecret,
            EmailAlwaysVerified = options.EmailAlwaysVerified,
        });
    }

    /// <summary>Registers a custom OIDC provider from an explicit configuration, wiring a named
    /// <see cref="HttpClient"/> for it.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <returns>The same builder.</returns>
    public ExternalAuthBuilder AddOidc(OidcProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        services.AddHttpClient(OidcExternalAuthProvider.HttpClientPrefix + config.Name)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // The discovery/JWKS client is held for the singleton provider's lifetime (inside its
                // ConfigurationManager), so it cannot rely on IHttpClientFactory handler rotation. Bound
                // connection age at the socket level so DNS/endpoint changes are still picked up.
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            });
        services.AddSingleton<IExternalAuthProvider>(sp => new OidcExternalAuthProvider(
            config,
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>()));
        return this;
    }

    /// <summary>Registers a custom provider instance.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The same builder.</returns>
    public ExternalAuthBuilder AddProvider(IExternalAuthProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        services.AddSingleton(provider);
        return this;
    }

    /// <summary>Registers a custom provider type, resolved from the container.</summary>
    /// <typeparam name="TProvider">The provider implementation type.</typeparam>
    /// <returns>The same builder.</returns>
    public ExternalAuthBuilder AddProvider<TProvider>()
        where TProvider : class, IExternalAuthProvider
    {
        services.AddSingleton<IExternalAuthProvider, TProvider>();
        return this;
    }
}
