using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.External;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests.External;

public sealed class OidcExternalAuthProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-15T00:00:00Z");
    private static readonly Uri TokenEndpoint = new("https://idp.test/token");
    private static readonly Uri MetadataAddress = new("https://idp.test/.well-known/openid-configuration");
    private static readonly Uri JwksUri = new("https://idp.test/jwks");
    private const string Issuer = "https://idp.test";
    private const string ClientId = "client-123";

    private static FakeTimeProvider Clock() => new(Now);

    private static ExternalAuthRequest Request() =>
        new("auth-code", "https://app.test/callback", "pkce-verifier");

    // ----- symmetric (HS256 / LINE-style) --------------------------------------------------------

    private static OidcProviderConfig SymmetricConfig(string secret, bool emailAlwaysVerified = true) => new()
    {
        Name = "line",
        TokenEndpoint = TokenEndpoint,
        ClientId = ClientId,
        ClientSecret = "channel-secret",
        Issuer = Issuer,
        Audience = ClientId,
        SymmetricSecret = secret,
        EmailAlwaysVerified = emailAlwaysVerified,
    };

    private static HttpClient HttpClientReturning(StubHttpMessageHandler handler) => new(handler);

    private static OidcExternalAuthProvider Provider(OidcProviderConfig config, HttpClient client, FakeTimeProvider clock) =>
        new(config, new SingleClientFactory(client), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    private static string TokenResponse(string idToken) =>
        JsonSerializer.Serialize(new { id_token = idToken, access_token = "at", token_type = "Bearer" });

    [Fact]
    public async Task ExchangeAsync_symmetric_success_returns_normalized_identity()
    {
        const string secret = "this-is-a-32-byte-minimum-secret!!";
        var idToken = TestIdTokens.SignHs256(secret, Issuer, ClientId, Now, Now.AddMinutes(5),
            new Dictionary<string, object>
            {
                ["sub"] = "U1234567890",
                ["email"] = "user@line.test",
                ["name"] = "LINE User",
            });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenResponse(idToken));
        var provider = Provider(SymmetricConfig(secret), HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.True(result.Succeeded);
        var identity = result.Identity!.Value;
        Assert.Equal("line", identity.Provider);
        Assert.Equal("U1234567890", identity.Subject);
        Assert.Equal("user@line.test", identity.Email);
        Assert.True(identity.EmailVerified); // EmailAlwaysVerified
        Assert.Equal("LINE User", identity.DisplayName);
    }

    [Fact]
    public async Task ExchangeAsync_posts_authorization_code_grant_with_pkce_verifier()
    {
        const string secret = "this-is-a-32-byte-minimum-secret!!";
        var idToken = TestIdTokens.SignHs256(secret, Issuer, ClientId, Now, Now.AddMinutes(5),
            new Dictionary<string, object> { ["sub"] = "U1" });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenResponse(idToken));
        var provider = Provider(SymmetricConfig(secret), HttpClientReturning(handler), Clock());

        await provider.ExchangeAsync(Request());

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("grant_type=authorization_code", body);
        Assert.Contains("code=auth-code", body);
        Assert.Contains("code_verifier=pkce-verifier", body);
        Assert.Contains("client_id=client-123", body);
    }

    [Fact]
    public async Task ExchangeAsync_symmetric_bad_signature_fails()
    {
        const string realSecret = "this-is-a-32-byte-minimum-secret!!";
        const string wrongSecret = "a-totally-different-32-byte-secret!";
        var idToken = TestIdTokens.SignHs256(wrongSecret, Issuer, ClientId, Now, Now.AddMinutes(5),
            new Dictionary<string, object> { ["sub"] = "U1" });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenResponse(idToken));
        var provider = Provider(SymmetricConfig(realSecret), HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExchangeAsync_wrong_issuer_fails()
    {
        const string secret = "this-is-a-32-byte-minimum-secret!!";
        var idToken = TestIdTokens.SignHs256(secret, "https://evil.test", ClientId, Now, Now.AddMinutes(5),
            new Dictionary<string, object> { ["sub"] = "U1" });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenResponse(idToken));
        var provider = Provider(SymmetricConfig(secret), HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExchangeAsync_wrong_audience_fails()
    {
        const string secret = "this-is-a-32-byte-minimum-secret!!";
        var idToken = TestIdTokens.SignHs256(secret, Issuer, "some-other-client", Now, Now.AddMinutes(5),
            new Dictionary<string, object> { ["sub"] = "U1" });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenResponse(idToken));
        var provider = Provider(SymmetricConfig(secret), HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExchangeAsync_expired_token_fails()
    {
        const string secret = "this-is-a-32-byte-minimum-secret!!";
        // Issued and expired well before "Now".
        var idToken = TestIdTokens.SignHs256(secret, Issuer, ClientId,
            Now.AddHours(-2), Now.AddHours(-1),
            new Dictionary<string, object> { ["sub"] = "U1" });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenResponse(idToken));
        var provider = Provider(SymmetricConfig(secret), HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExchangeAsync_token_with_no_expiry_fails()
    {
        const string secret = "this-is-a-32-byte-minimum-secret!!";
        // OIDC mandates `exp` on an id_token; a token without one must be rejected.
        var idToken = TestIdTokens.SignHs256NoExpiry(secret, Issuer, ClientId,
            new Dictionary<string, object> { ["sub"] = "U1" });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenResponse(idToken));
        var provider = Provider(SymmetricConfig(secret), HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExchangeAsync_token_endpoint_non_2xx_fails()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { error = "invalid_grant" }));
        var provider = Provider(SymmetricConfig("this-is-a-32-byte-minimum-secret!!"),
            HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExchangeAsync_missing_id_token_fails()
    {
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK,
            JsonSerializer.Serialize(new { access_token = "at", token_type = "Bearer" }));
        var provider = Provider(SymmetricConfig("this-is-a-32-byte-minimum-secret!!"),
            HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExchangeAsync_email_verified_false_when_provider_reports_false()
    {
        const string secret = "this-is-a-32-byte-minimum-secret!!";
        var idToken = TestIdTokens.SignHs256(secret, Issuer, ClientId, Now, Now.AddMinutes(5),
            new Dictionary<string, object>
            {
                ["sub"] = "G1",
                ["email"] = "user@gmail.test",
                ["email_verified"] = false,
            });
        var handler = StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenResponse(idToken));
        var config = new OidcProviderConfig
        {
            Name = "google",
            TokenEndpoint = TokenEndpoint,
            ClientId = ClientId,
            ClientSecret = "secret",
            Issuer = Issuer,
            Audience = ClientId,
            SymmetricSecret = secret,
            EmailAlwaysVerified = false,
        };
        var provider = Provider(config, HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.True(result.Succeeded);
        Assert.False(result.Identity!.Value.EmailVerified);
    }

    // ----- asymmetric (RS256 / JWKS / Google-style) ----------------------------------------------

    private static OidcProviderConfig AsymmetricConfig() => new()
    {
        Name = "google",
        TokenEndpoint = TokenEndpoint,
        ClientId = ClientId,
        ClientSecret = "client-secret",
        Issuer = Issuer,
        Audience = ClientId,
        MetadataAddress = MetadataAddress,
    };

    /// <summary>The OIDC discovery document the provider fetches before the JWKS, pointing the key
    /// retrieval at <see cref="JwksUri"/>.</summary>
    private static string DiscoveryJson() =>
        JsonSerializer.Serialize(new { issuer = Issuer, jwks_uri = JwksUri.AbsoluteUri });

    /// <summary>Routes the token POST, the OIDC discovery GET, and the JWKS GET to the correct canned
    /// response by URL, so the asymmetric path runs hermetically against a stub.</summary>
    private static StubHttpMessageHandler RoutingHandler(string tokenJson, string jwksJson)
    {
        var discoveryJson = DiscoveryJson();
        return new StubHttpMessageHandler(req =>
        {
            string body;
            if (req.RequestUri == MetadataAddress)
            {
                body = discoveryJson;
            }
            else if (req.RequestUri == JwksUri)
            {
                body = jwksJson;
            }
            else
            {
                body = tokenJson;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        });
    }

    [Fact]
    public async Task ExchangeAsync_asymmetric_success_returns_normalized_identity()
    {
        var keys = TestIdTokens.NewRsaKey();
        var idToken = TestIdTokens.SignRs256(keys, Issuer, ClientId, Now, Now.AddMinutes(5),
            new Dictionary<string, object>
            {
                ["sub"] = "G1",
                ["email"] = "user@gmail.test",
                ["email_verified"] = true,
                ["name"] = "Google User",
            });
        var handler = RoutingHandler(TokenResponse(idToken), keys.JwksJson);
        var provider = Provider(AsymmetricConfig(), HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.True(result.Succeeded);
        var identity = result.Identity!.Value;
        Assert.Equal("google", identity.Provider);
        Assert.Equal("G1", identity.Subject);
        Assert.Equal("user@gmail.test", identity.Email);
        Assert.True(identity.EmailVerified);
        Assert.Equal("Google User", identity.DisplayName);
    }

    [Fact]
    public async Task ExchangeAsync_asymmetric_bad_signature_fails()
    {
        var signingKeys = TestIdTokens.NewRsaKey();
        var publishedKeys = TestIdTokens.NewRsaKey(); // JWKS advertises a DIFFERENT key
        var idToken = TestIdTokens.SignRs256(signingKeys, Issuer, ClientId, Now, Now.AddMinutes(5),
            new Dictionary<string, object> { ["sub"] = "G1" });
        var handler = RoutingHandler(TokenResponse(idToken), publishedKeys.JwksJson);
        var provider = Provider(AsymmetricConfig(), HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ExchangeAsync_asymmetric_recovers_after_key_rotation()
    {
        // The IdP has rotated keys: the cached JWKS serves the OLD key, but the token is signed with
        // the NEW key. The provider must refresh the JWKS and validate against the rotated key.
        var rotatedKey = TestIdTokens.NewRsaKey();
        var staleKey = TestIdTokens.NewRsaKey();
        var idToken = TestIdTokens.SignRs256(rotatedKey, Issuer, ClientId, Now, Now.AddMinutes(5),
            new Dictionary<string, object> { ["sub"] = "G1", ["email"] = "user@gmail.test" });

        var discoveryJson = JsonSerializer.Serialize(new { issuer = Issuer, jwks_uri = JwksUri.AbsoluteUri });
        var tokenJson = TokenResponse(idToken);
        var jwksFetches = 0;
        var handler = new StubHttpMessageHandler(req =>
        {
            string body;
            if (req.RequestUri == MetadataAddress)
            {
                body = discoveryJson;
            }
            else if (req.RequestUri == JwksUri)
            {
                // First JWKS fetch serves the stale key; after rotation it serves the new signing key.
                body = Interlocked.Increment(ref jwksFetches) == 1 ? staleKey.JwksJson : rotatedKey.JwksJson;
            }
            else
            {
                body = tokenJson;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var provider = Provider(AsymmetricConfig(), HttpClientReturning(handler), Clock());

        var result = await provider.ExchangeAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal("G1", result.Identity!.Value.Subject);
        Assert.True(jwksFetches >= 2); // refreshed after the first (stale) attempt failed
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> that always hands back the same client.</summary>
internal sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
