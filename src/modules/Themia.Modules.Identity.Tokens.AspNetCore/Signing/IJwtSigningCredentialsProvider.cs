using Microsoft.IdentityModel.Tokens;

namespace Themia.Modules.Identity.Tokens.AspNetCore.Signing;

/// <summary>Supplies the signing credentials used to mint access tokens and the key material used to
/// validate them. The default is HS256 symmetric; an RS256/ES256 + JWKS provider can replace it via DI
/// without touching callers.</summary>
public interface IJwtSigningCredentialsProvider
{
    /// <summary>The credentials used to sign newly minted tokens.</summary>
    SigningCredentials SigningCredentials { get; }

    /// <summary>The key used to validate incoming tokens.</summary>
    SecurityKey ValidationKey { get; }
}
