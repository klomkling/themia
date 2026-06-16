using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.AspNetCore.External;

/// <summary>An <see cref="IExternalAuthProvider"/> that performs the OAuth/OIDC server-side code
/// exchange and validates the returned id_token. Supports two signing-key strategies:
/// RS256 via OIDC discovery (<see cref="OidcProviderConfig.MetadataAddress"/>), or HS256 via a shared
/// secret (<see cref="OidcProviderConfig.SymmetricSecret"/>).</summary>
/// <remarks>Reuses the same <see cref="JsonWebTokenHandler"/> validation idioms as the 0.5.1 access
/// token slice (<c>MapInboundClaims=false</c>, explicit issuer/audience/lifetime), and a fixed
/// <see cref="TimeProvider"/> for deterministic lifetime validation. The asymmetric path resolves and
/// auto-refreshes signing keys through <see cref="ConfigurationManager{T}"/>, so a key rotation at the
/// IdP recovers without a process restart.</remarks>
public sealed class OidcExternalAuthProvider : IExternalAuthProvider
{
    /// <summary>The named-<see cref="HttpClient"/> prefix; the suffix is the provider name.</summary>
    internal const string HttpClientPrefix = "Themia.Identity.ExternalAuth:";

    private static readonly JsonWebTokenHandler Handler = new();

    private readonly OidcProviderConfig config;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OidcExternalAuthProvider> logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? configManager;

    /// <summary>Creates the provider.</summary>
    /// <param name="config">The provider configuration.</param>
    /// <param name="httpClientFactory">The HTTP client factory (named client per provider).</param>
    /// <param name="timeProvider">The time source used for lifetime validation.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <exception cref="ArgumentException">Both or neither signing-key strategies are configured.</exception>
    public OidcExternalAuthProvider(
        OidcProviderConfig config,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var hasMetadata = config.MetadataAddress is not null;
        var hasSymmetric = !string.IsNullOrEmpty(config.SymmetricSecret);
        if (hasMetadata == hasSymmetric)
        {
            throw new ArgumentException(
                "Exactly one signing-key strategy must be set: MetadataAddress (RS256) or SymmetricSecret (HS256).",
                nameof(config));
        }

        this.config = config;
        this.httpClientFactory = httpClientFactory;
        this.timeProvider = timeProvider;
        this.logger = loggerFactory.CreateLogger<OidcExternalAuthProvider>();

        if (hasMetadata)
        {
            // The OIDC discovery doc is fetched (and auto-refreshed on its interval) through the
            // provider's named HttpClient, so tests can route discovery + JWKS to a stub.
            var documentRetriever = new HttpDocumentRetriever(
                httpClientFactory.CreateClient(HttpClientPrefix + config.Name));
            configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                config.MetadataAddress!.AbsoluteUri,
                new OpenIdConnectConfigurationRetriever(),
                documentRetriever);
        }
    }

    /// <inheritdoc />
    public string Name => config.Name;

    /// <summary>The clock-skew tolerance applied during id_token lifetime validation.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<ExternalAuthResult> ExchangeAsync(
        ExternalAuthRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientPrefix + config.Name);

        var idToken = await ExchangeCodeForIdTokenAsync(client, request, cancellationToken);
        if (idToken is null)
        {
            return ExternalAuthResult.Failed("token_endpoint_rejected");
        }

        var validation = await ValidateIdTokenAsync(idToken, cancellationToken);
        if (validation is not { } validated)
        {
            return ExternalAuthResult.Failed("id_token_invalid");
        }

        return MapIdentity(validated);
    }

    private async Task<string?> ExchangeCodeForIdTokenAsync(
        HttpClient client,
        ExternalAuthRequest request,
        CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", request.Code),
            new("redirect_uri", request.RedirectUri),
            new("client_id", config.ClientId),
            new("client_secret", config.ClientSecret),
        };
        if (!string.IsNullOrEmpty(request.CodeVerifier))
        {
            form.Add(new KeyValuePair<string, string>("code_verifier", request.CodeVerifier));
        }

        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(config.TokenEndpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Expected failure (invalid_grant, etc.) — typed result, not an exception. Never log the body.
            logger.LogInformation(
                "External provider {Provider} token exchange returned {StatusCode}.",
                config.Name, (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.TryGetProperty("id_token", out var idTokenElement) &&
            idTokenElement.ValueKind == JsonValueKind.String)
        {
            return idTokenElement.GetString();
        }

        logger.LogInformation("External provider {Provider} token response had no id_token.", config.Name);
        return null;
    }

    private async Task<JsonWebToken?> ValidateIdTokenAsync(
        string idToken,
        CancellationToken cancellationToken)
    {
        // Symmetric (HS256) keys are static; resolve once and validate.
        if (config.SymmetricSecret is { } secret)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            return await ValidateWithKeysAsync(idToken, [key]);
        }

        // Asymmetric (RS256): keys come from OIDC discovery + JWKS, auto-refreshed on rotation.
        var signingKeys = await GetSigningKeysAsync(cancellationToken);
        var validated = await ValidateWithKeysAsync(idToken, signingKeys);
        if (validated is not null)
        {
            return validated;
        }

        // A rotation race (token signed by a new key not yet in our cached metadata) looks like a
        // signature failure. Force a metadata refresh and retry exactly once so login recovers.
        configManager!.RequestRefresh();
        var refreshedKeys = await GetSigningKeysAsync(cancellationToken);
        return await ValidateWithKeysAsync(idToken, refreshedKeys);
    }

    private async Task<ICollection<SecurityKey>> GetSigningKeysAsync(CancellationToken cancellationToken)
    {
        var configuration = await configManager!.GetConfigurationAsync(cancellationToken);
        return configuration.SigningKeys;
    }

    private async Task<JsonWebToken?> ValidateWithKeysAsync(
        string idToken,
        IEnumerable<SecurityKey> signingKeys)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,
            ValidateAudience = true,
            ValidAudience = config.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidateLifetime = true,
            // Validate lifetime against the injected clock (deterministic + testable), applying skew.
            LifetimeValidator = ValidateLifetime,
        };

        var result = await Handler.ValidateTokenAsync(idToken, parameters);
        if (!result.IsValid)
        {
            logger.LogInformation(
                "External provider {Provider} id_token failed validation.", config.Name);
            return null;
        }

        return (JsonWebToken)result.SecurityToken;
    }

    private bool ValidateLifetime(
        DateTime? notBefore,
        DateTime? expires,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (notBefore is { } nbf && now + ClockSkew < nbf)
        {
            return false;
        }

        // OIDC mandates `exp` on an id_token; reject a token that omits it rather than waving it through.
        if (expires is not { } exp)
        {
            return false;
        }

        return now - ClockSkew <= exp;
    }

    private ExternalAuthResult MapIdentity(JsonWebToken token)
    {
        var subject = GetClaim(token, config.SubjectClaim);
        if (string.IsNullOrEmpty(subject))
        {
            return ExternalAuthResult.Failed("id_token_missing_subject");
        }

        var email = GetClaim(token, config.EmailClaim);
        var displayName = GetClaim(token, config.NameClaim);
        var emailVerified = config.EmailAlwaysVerified || ReadBool(token, config.EmailVerifiedClaim);

        return ExternalAuthResult.Success(new ExternalIdentity(
            config.Name,
            subject,
            string.IsNullOrEmpty(email) ? null : email,
            emailVerified,
            string.IsNullOrEmpty(displayName) ? null : displayName));
    }

    private static string? GetClaim(JsonWebToken token, string name) =>
        token.TryGetPayloadValue<string>(name, out var value) ? value : null;

    private static bool ReadBool(JsonWebToken token, string name) =>
        token.TryGetPayloadValue<bool>(name, out var value) && value;
}
