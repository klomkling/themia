using System.Security.Cryptography;
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
/// <see cref="TimeProvider"/> for deterministic lifetime validation. The asymmetric path resolves
/// signing keys through a cached <see cref="ConfigurationManager{T}"/>; on a signature failure that looks
/// like a key rotation it fetches fresh metadata + JWKS directly and retries once, so a rotation at the
/// IdP recovers within the same request without a process restart.</remarks>
public sealed class OidcExternalAuthProvider : IExternalAuthProvider
{
    /// <summary>The named-<see cref="HttpClient"/> prefix; the suffix is the provider name.</summary>
    internal const string HttpClientPrefix = "Themia.Identity.ExternalAuth:";

    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>The signing algorithms accepted on the asymmetric (JWKS/metadata) path.</summary>
    private static readonly string[] AsymmetricAlgorithms = [SecurityAlgorithms.RsaSha256];

    /// <summary>The signing algorithms accepted on the symmetric (shared-secret) path.</summary>
    private static readonly string[] SymmetricAlgorithms = [SecurityAlgorithms.HmacSha256];

    private readonly OidcProviderConfig config;
    private readonly IHttpClientFactory httpClientFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OidcExternalAuthProvider> logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? configManager;
    private readonly HttpDocumentRetriever? documentRetriever;

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
            // provider's named HttpClient, so tests can route discovery + JWKS to a stub. This client is
            // held for the provider's (singleton) lifetime by ConfigurationManager; the named client's
            // handler is configured with a bounded PooledConnectionLifetime (see ExternalAuthBuilder) so
            // socket-level connection age is recycled despite not rotating the HttpClient instance.
            documentRetriever = new HttpDocumentRetriever(
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

        // Nonce binding: if the id_token carries a `nonce` claim, the client MUST supply the matching
        // value, and if the client supplies a nonce, the token MUST carry the matching one. Binding to the
        // token (not just to whether the client opted in) closes the replay where an attacker omits the
        // nonce field to skip the check on a token that actually asserts one. Only when neither side has a
        // nonce is the check skipped.
        if (!NonceSatisfied(validated, request.Nonce))
        {
            logger.LogInformation("External provider {Provider} id_token nonce did not match.", config.Name);
            return ExternalAuthResult.Failed("nonce_mismatch");
        }

        return MapIdentity(validated);
    }

    private static bool NonceSatisfied(JsonWebToken token, string? expected)
    {
        var actual = GetClaim(token, "nonce");
        var hasActual = !string.IsNullOrEmpty(actual);
        var hasExpected = !string.IsNullOrEmpty(expected);
        if (!hasActual && !hasExpected)
        {
            return true; // neither side asserts a nonce
        }

        return hasActual && hasExpected
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(actual!), Encoding.UTF8.GetBytes(expected!));
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
        // Symmetric (HS256) keys are static; resolve once and validate. The algorithm allow-list pins
        // HS256 so a token signed with any other algorithm is rejected up front.
        if (config.SymmetricSecret is { } secret)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            return await ValidateWithKeysAsync(idToken, [key], SymmetricAlgorithms);
        }

        // Asymmetric (RS256): keys come from OIDC discovery + JWKS, auto-refreshed on rotation. The
        // algorithm allow-list pins RS256, blocking alg-confusion (an HS256 token signed with the public
        // key as a MAC secret) and alg:none by allow-list rather than relying on key typing alone.
        var signingKeys = await GetSigningKeysAsync(cancellationToken);
        var validated = await ValidateWithKeysAsync(idToken, signingKeys, AsymmetricAlgorithms);
        if (validated is not null)
        {
            return validated;
        }

        // A rotation race (token signed by a new key not yet in our cached metadata) looks like a
        // signature failure. Fetch fresh metadata + JWKS directly and retry exactly once so login
        // recovers immediately. We bypass ConfigurationManager.RequestRefresh() for this retry because
        // since IdentityModel 8.x it is rate-limited by RefreshInterval (a refresh-flooding guard), so an
        // in-request forced refresh is a no-op until the interval elapses. Reaching this path requires a
        // *successful* token-endpoint code exchange, so the direct fetch is not an unauthenticated refresh
        // vector. We still nudge the cached manager so subsequent logins pick up the rotated keys.
        var refreshed = await OpenIdConnectConfigurationRetriever.GetAsync(
            config.MetadataAddress!.AbsoluteUri, documentRetriever!, cancellationToken);
        configManager!.RequestRefresh();
        return await ValidateWithKeysAsync(idToken, refreshed.SigningKeys, AsymmetricAlgorithms);
    }

    private async Task<ICollection<SecurityKey>> GetSigningKeysAsync(CancellationToken cancellationToken)
    {
        var configuration = await configManager!.GetConfigurationAsync(cancellationToken);
        return configuration.SigningKeys;
    }

    private async Task<JsonWebToken?> ValidateWithKeysAsync(
        string idToken,
        IEnumerable<SecurityKey> signingKeys,
        IEnumerable<string> validAlgorithms)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,
            ValidateAudience = true,
            ValidAudience = config.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            // Pin the accepted signing algorithm(s) by allow-list, blocking alg-confusion and alg:none.
            ValidAlgorithms = validAlgorithms,
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

    private static bool ReadBool(JsonWebToken token, string name)
    {
        if (token.TryGetPayloadValue<bool>(name, out var value))
        {
            return value;
        }

        // Some OIDC providers serialize a boolean claim (e.g. email_verified) as a JSON string
        // ("true"/"false") rather than a real boolean; accept that form too.
        return token.TryGetPayloadValue<string>(name, out var text)
            && bool.TryParse(text, out var parsed) && parsed;
    }
}
