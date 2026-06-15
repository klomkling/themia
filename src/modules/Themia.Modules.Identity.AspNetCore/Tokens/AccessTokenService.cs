using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.Options;
using Themia.Modules.Identity.AspNetCore.Signing;

namespace Themia.Modules.Identity.AspNetCore.Tokens;

/// <summary>Default <see cref="IAccessTokenService"/>. Mints a signed JWT from the principal's claims,
/// stamping issuer/audience/expiry from <see cref="JwtOptions"/>.</summary>
public sealed class AccessTokenService : IAccessTokenService
{
    private readonly IJwtSigningCredentialsProvider _credentials;
    private readonly JwtOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="credentials">The signing-credentials provider.</param>
    /// <param name="options">The JWT options.</param>
    /// <param name="timeProvider">The time source.</param>
    public AccessTokenService(IJwtSigningCredentialsProvider credentials, JwtOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _credentials = credentials;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public AccessToken Issue(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var now = _timeProvider.GetUtcNow();
        var expires = now.Add(_options.AccessTokenLifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = principal.Identity as ClaimsIdentity ?? new ClaimsIdentity(principal.Claims),
            SigningCredentials = _credentials.SigningCredentials,
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new AccessToken(token, expires);
    }
}
