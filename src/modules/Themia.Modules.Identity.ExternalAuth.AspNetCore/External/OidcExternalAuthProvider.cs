using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.External;

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
internal sealed class OidcExternalAuthProvider : IExternalAuthProvider
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
                documentRetriever)
            {
                // Floor the refresh cooldown so the post-rotation RequestRefresh() actually re-primes the
                // cache on a later poll (default RefreshInterval is 5 min); the auth-code-gated direct
                // fetch already handles the in-request rotation, this just stops every subsequent login
                // taking that direct path until the 12 h automatic refresh.
                RefreshInterval = ConfigurationManager<OpenIdConnectConfiguration>.MinimumRefreshInterval,
            };
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
            return TokenOrNull(await ValidateCoreAsync(idToken, [key], SymmetricAlgorithms));
        }

        // Asymmetric (RS256): keys come from OIDC discovery + JWKS. The algorithm allow-list pins RS256,
        // blocking alg-confusion (an HS256 token signed with the public key as a MAC secret) and alg:none
        // by allow-list rather than relying on key typing alone.
        var configuration = await configManager!.GetConfigurationAsync(cancellationToken);
        var result = await ValidateCoreAsync(idToken, configuration.SigningKeys, AsymmetricAlgorithms);
        if (result.IsValid)
        {
            return (JsonWebToken)result.SecurityToken;
        }

        // Only a *missing signing key* looks like an IdP key rotation; other failures (expired,
        // wrong issuer/audience, tampered signature) can't be cured by refetching keys, so don't.
        if (result.Exception is not SecurityTokenSignatureKeyNotFoundException)
        {
            return TokenOrNull(result);
        }

        // Rotation race: the token is signed by a key not yet in our cached metadata. Fetch fresh
        // metadata + JWKS directly and retry once so login recovers in-request. We bypass the cached
        // ConfigurationManager here because since IdentityModel 8.x its RequestRefresh() is rate-limited
        // by RefreshInterval (a refresh-flooding guard), so an in-request forced refresh is a no-op until
        // the interval elapses. Reaching this path requires a *successful* token-endpoint code exchange,
        // so the direct fetch is not an unauthenticated refresh vector.
        OpenIdConnectConfiguration refreshed;
        try
        {
            refreshed = await OpenIdConnectConfigurationRetriever.GetAsync(
                config.MetadataAddress!.AbsoluteUri, documentRetriever!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // genuine caller cancellation — propagate
        }
        catch (Exception ex)
        {
            // A transient metadata/JWKS fetch failure is an expected operational error, not a server
            // fault: surface it as an invalid id_token (caller maps to a clean auth failure), don't throw.
            // Gating on the token (not the exception type) keeps an HttpClient request-timeout — which
            // surfaces as a TaskCanceledException unrelated to our token — on this degradation path.
            logger.LogWarning(
                ex, "External provider {Provider} metadata refresh after a suspected key rotation failed.",
                config.Name);
            return null;
        }

        logger.LogInformation(
            "External provider {Provider} refreshed signing keys after a suspected key rotation.", config.Name);
        // Nudge the cached manager so it picks up the rotated keys on a later poll (RefreshInterval is set
        // to the minimum in the constructor so this isn't perpetually rate-limited).
        configManager!.RequestRefresh();
        return TokenOrNull(await ValidateCoreAsync(idToken, refreshed.SigningKeys, AsymmetricAlgorithms));
    }

    private async Task<TokenValidationResult> ValidateCoreAsync(
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

        return await Handler.ValidateTokenAsync(idToken, parameters);
    }

    private JsonWebToken? TokenOrNull(TokenValidationResult result)
    {
        if (!result.IsValid)
        {
            // Log the failure class (type name only — never the token) to aid diagnosis without leaking.
            logger.LogInformation(
                "External provider {Provider} id_token failed validation ({Reason}).",
                config.Name, result.Exception?.GetType().Name ?? "unknown");
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
