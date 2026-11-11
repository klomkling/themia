using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.ExternalAuth.AspNetCore.External;
using Xunit;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests.External;

public sealed class FederatedMfaProviderTests
{
    private const string Secret = "this-is-a-32-byte-minimum-secret!!";
    private const string Issuer = "https://idp.test";
    private const string ClientId = "client-123";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-15T00:00:00Z");

    private static OidcProviderConfig Config(bool trusts) => new()
    {
        Name = "partner-idp",
        TokenEndpoint = new Uri("https://idp.test/token"),
        ClientId = ClientId,
        ClientSecret = "secret",
        Issuer = Issuer,
        Audience = ClientId,
        SymmetricSecret = Secret,
        TrustsFederatedMfa = trusts,
    };

    private static async Task<ExternalIdentity> ExchangeAsync(bool trusts, IDictionary<string, object> extraClaims)
    {
        var claims = new Dictionary<string, object> { ["sub"] = "u1", ["email"] = "a@b.test" };
        foreach (var pair in extraClaims)
        {
            claims[pair.Key] = pair.Value;
        }

        var idToken = TestIdTokens.SignHs256(Secret, Issuer, ClientId, Now, Now.AddMinutes(5), claims);
        var body = JsonSerializer.Serialize(new { id_token = idToken, access_token = "at", token_type = "Bearer" });
        var provider = new OidcExternalAuthProvider(
            Config(trusts), new SingleClientFactory(new HttpClient(StubHttpMessageHandler.Json(HttpStatusCode.OK, body))),
            new FakeTimeProvider(Now), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var result = await provider.ExchangeAsync(new ExternalAuthRequest("code", "https://app.test/cb", "v"));
        Assert.True(result.Succeeded);
        return result.Identity!.Value;
    }

    private static Dictionary<string, object> Claims(object amr, object? authTime) =>
        new() { ["amr"] = amr, ["auth_time"] = authTime ?? Now.AddMinutes(-1).ToUnixTimeSeconds() };

    [Fact]
    public async Task Opted_in_provider_with_strong_fresh_mfa_yields_a_verified_proof_with_known_values_only()
    {
        var identity = await ExchangeAsync(true, Claims(new[] { "otp", "mfa", "made-up" }, null));

        var proof = identity.VerifiedSecondFactor!;
        Assert.Equal(["otp", "mfa"], proof.Amr);
        Assert.Equal(Now.AddMinutes(-1), proof.AuthenticatedAt);
    }

    [Fact]
    public async Task Provider_that_does_not_opt_in_never_yields_a_proof_even_with_perfect_claims()
    {
        var identity = await ExchangeAsync(false, Claims(new[] { "otp", "mfa" }, null));

        Assert.Null(identity.VerifiedSecondFactor);
    }

    [Theory]
    [InlineData("mfa")]
    [InlineData("sms")]
    [InlineData("otp")]
    public async Task Weak_only_or_mfa_less_amr_yields_no_proof(string only)
    {
        var identity = await ExchangeAsync(true, Claims(new[] { only }, null));

        Assert.Null(identity.VerifiedSecondFactor);
    }

    [Fact]
    public async Task Stale_auth_time_yields_no_proof()
    {
        var identity = await ExchangeAsync(true, Claims(new[] { "otp", "mfa" }, Now.AddHours(-2).ToUnixTimeSeconds()));

        Assert.Null(identity.VerifiedSecondFactor);
    }

    [Fact]
    public async Task Missing_auth_time_yields_no_proof()
    {
        var identity = await ExchangeAsync(true, new Dictionary<string, object> { ["amr"] = new[] { "otp", "mfa" } });

        Assert.Null(identity.VerifiedSecondFactor);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData(-5L)]
    [InlineData(9_999_999_999_999L)]
    public async Task Malformed_auth_time_yields_no_proof_and_does_not_throw(object authTime)
    {
        var identity = await ExchangeAsync(true, Claims(new[] { "otp", "mfa" }, authTime));

        Assert.Null(identity.VerifiedSecondFactor);
    }

    [Fact]
    public async Task Amr_of_the_wrong_json_type_yields_no_proof()
    {
        var identity = await ExchangeAsync(true, Claims(42, null));

        Assert.Null(identity.VerifiedSecondFactor);
    }
}
