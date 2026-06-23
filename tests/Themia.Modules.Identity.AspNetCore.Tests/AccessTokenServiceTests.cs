using System.Security.Claims;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Tokens.AspNetCore.Options;
using Themia.Modules.Identity.Tokens.AspNetCore.Signing;
using Themia.Modules.Identity.Tokens.AspNetCore.Tokens;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class AccessTokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        SigningKey = new string('k', 32),
        Issuer = "themia",
        Audience = "themia-clients",
        AccessTokenLifetime = TimeSpan.FromMinutes(15),
    };

    private static AccessTokenService NewService(TimeProvider time) =>
        new(new SymmetricSigningCredentialsProvider(Options), Options, time);

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Bearer", ClaimTypes.Name, ClaimTypes.Role));

    [Fact]
    public async Task Issue_emits_a_validatable_jwt_with_standard_sub_name_and_role_claims()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T00:00:00Z"));
        var service = NewService(time);
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, "11111111-1111-1111-1111-111111111111"),
            new Claim(ClaimTypes.Name, "alice"),
            new Claim(ClaimTypes.Role, "admin"));

        var token = service.Issue(principal);

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token.Token, new TokenValidationParameters
        {
            ValidIssuer = Options.Issuer,
            ValidAudience = Options.Audience,
            IssuerSigningKey = new SymmetricSigningCredentialsProvider(Options).ValidationKey,
            ValidateLifetime = false,
        });

        Assert.True(result.IsValid);
        Assert.Equal(token.ExpiresAt, time.GetUtcNow().Add(Options.AccessTokenLifetime));

        // The wire format uses standard short claim names, not the long .NET ClaimTypes URIs.
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal("11111111-1111-1111-1111-111111111111", jwt.GetClaim("sub").Value);
        Assert.Equal("alice", jwt.GetClaim("name").Value);
        Assert.Contains(jwt.Claims, c => c.Type == "role" && c.Value == "admin");

        // The long .NET URIs must NOT appear on the wire (no double "sub"/NameIdentifier).
        Assert.DoesNotContain(jwt.Claims, c => c.Type == ClaimTypes.NameIdentifier);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == ClaimTypes.Role);
    }

    [Fact]
    public void Issue_carries_only_the_claims_in_the_principal()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T00:00:00Z"));
        var service = NewService(time);
        var token = service.Issue(Principal(new Claim(ClaimTypes.NameIdentifier, "u")));

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.Token);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "themia:tenant_id");
    }
}
