using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.External;
using Themia.Modules.Identity.AspNetCore.Options;

namespace Themia.Modules.Identity.AspNetCore.DependencyInjection;

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
        return new ExternalAuthBuilder(services);
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

        services.AddHttpClient(OidcExternalAuthProvider.HttpClientPrefix + config.Name);
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
