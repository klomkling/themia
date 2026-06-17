using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Themia.Modules.Identity.AspNetCore.Tests.External;

/// <summary>Test helpers that mint id_tokens (HS256 for the symmetric path, RS256 + a stub JWKS
/// document for the asymmetric path) so the provider can be exercised end-to-end without a live IdP.</summary>
internal static class TestIdTokens
{
    /// <summary>Mints an HS256-signed id_token from the given claims.</summary>
    public static string SignHs256(
        string secret,
        string issuer,
        string audience,
        DateTimeOffset notBefore,
        DateTimeOffset expires,
        IDictionary<string, object> claims)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var descriptor = Descriptor(issuer, audience, notBefore, expires, claims,
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Mints an HS256-signed id_token with NO <c>exp</c> claim (an OIDC violation), to assert
    /// the provider rejects tokens that omit an expiry.</summary>
    public static string SignHs256NoExpiry(
        string secret,
        string issuer,
        string audience,
        IDictionary<string, object> claims)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };
        // Suppress the handler's default exp/nbf/iat so the minted token genuinely has no expiry.
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(descriptor);
    }

    /// <summary>Mints an unsigned (<c>alg:none</c>) JWT: a header with <c>"alg":"none"</c>, a claims
    /// payload, and an empty signature segment. Used to assert the provider rejects unsigned tokens.</summary>
    public static string UnsignedNone(string issuer, string audience, DateTimeOffset expires, IDictionary<string, object> claims)
    {
        static string B64(string json) =>
            Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));

        var header = System.Text.Json.JsonSerializer.Serialize(new { alg = "none", typ = "JWT" });
        var payload = new Dictionary<string, object>(claims)
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["exp"] = expires.ToUnixTimeSeconds(),
        };
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payload);
        // Trailing dot: empty signature segment, as an alg:none JWT is unsigned.
        return $"{B64(header)}.{B64(payloadJson)}.";
    }

    /// <summary>An RSA key plus the matching single-key JWKS document, for the asymmetric path.</summary>
    public sealed record RsaKeyMaterial(RSA Rsa, RsaSecurityKey SecurityKey, string Kid, string JwksJson);

    /// <summary>Generates a fresh RSA key and the JWKS document a provider would fetch for it.</summary>
    public static RsaKeyMaterial NewRsaKey()
    {
        var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = Guid.NewGuid().ToString("N") };
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
        jwk.Use = "sig";
        jwk.Alg = SecurityAlgorithms.RsaSha256;
        var jwks = new JsonWebKeySet();
        jwks.Keys.Add(jwk);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { kty = jwk.Kty, use = jwk.Use, kid = jwk.Kid, alg = jwk.Alg, n = jwk.N, e = jwk.E },
            },
        });
        return new RsaKeyMaterial(rsa, key, key.KeyId, json);
    }

    /// <summary>Mints an RS256-signed id_token using the given RSA key material.</summary>
    public static string SignRs256(
        RsaKeyMaterial material,
        string issuer,
        string audience,
        DateTimeOffset notBefore,
        DateTimeOffset expires,
        IDictionary<string, object> claims)
    {
        var descriptor = Descriptor(issuer, audience, notBefore, expires, claims,
            new SigningCredentials(material.SecurityKey, SecurityAlgorithms.RsaSha256));
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static SecurityTokenDescriptor Descriptor(
        string issuer,
        string audience,
        DateTimeOffset notBefore,
        DateTimeOffset expires,
        IDictionary<string, object> claims,
        SigningCredentials credentials) => new()
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = notBefore.UtcDateTime,
            IssuedAt = notBefore.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = credentials,
        };
}
