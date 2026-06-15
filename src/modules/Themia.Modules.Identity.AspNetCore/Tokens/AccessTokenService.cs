using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.Options;
using Themia.Modules.Identity.AspNetCore.Signing;

namespace Themia.Modules.Identity.AspNetCore.Tokens;

/// <summary>Standard short JWT claim names emitted on the wire for OAuth2/OIDC/gateway interop.
/// The validated server-side principal still carries the long <see cref="ClaimTypes"/> URIs
/// (re-added on validation), so internal consumers (<c>ICurrentUser</c>, <c>[Authorize(Roles)]</c>,
/// the audit accessor) are unaffected.</summary>
internal static class JwtClaimNames
{
    /// <summary>Subject — maps from <see cref="ClaimTypes.NameIdentifier"/>.</summary>
    public const string Subject = JwtRegisteredClaimNames.Sub;

    /// <summary>Name — maps from <see cref="ClaimTypes.Name"/>.</summary>
    public const string Name = JwtRegisteredClaimNames.Name;

    /// <summary>Role — maps from <see cref="ClaimTypes.Role"/>. There is no registered "role" claim,
    /// so the conventional ASP.NET short name is used.</summary>
    public const string Role = "role";
}

/// <summary>Default <see cref="IAccessTokenService"/>. Mints a signed JWT from the principal's claims,
/// stamping issuer/audience/expiry from <see cref="JwtOptions"/>.</summary>
public sealed class AccessTokenService : IAccessTokenService
{
    private static readonly JsonWebTokenHandler Handler = new();

    private readonly IJwtSigningCredentialsProvider credentials;
    private readonly JwtOptions options;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the service.</summary>
    /// <param name="credentials">The signing-credentials provider.</param>
    /// <param name="options">The JWT options.</param>
    /// <param name="timeProvider">The time source.</param>
    public AccessTokenService(IJwtSigningCredentialsProvider credentials, JwtOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.credentials = credentials;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public AccessToken Issue(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var now = timeProvider.GetUtcNow();
        var expires = now.Add(options.AccessTokenLifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = BuildWireIdentity(principal),
            SigningCredentials = credentials.SigningCredentials,
        };

        var token = Handler.CreateToken(descriptor);
        return new AccessToken(token, expires);
    }

    /// <summary>Builds the token identity, mapping the three well-known .NET claims to standard JWT
    /// names (<c>sub</c>/<c>name</c>/<c>role</c>) and copying every other claim verbatim (the Themia
    /// namespaced claims are already external-safe and are read by their literal type internally).</summary>
    private static ClaimsIdentity BuildWireIdentity(ClaimsPrincipal principal)
    {
        var identity = new ClaimsIdentity();
        foreach (var claim in principal.Claims)
        {
            var type = claim.Type switch
            {
                ClaimTypes.NameIdentifier => JwtClaimNames.Subject,
                ClaimTypes.Name => JwtClaimNames.Name,
                ClaimTypes.Role => JwtClaimNames.Role,
                _ => claim.Type,
            };
            identity.AddClaim(new Claim(type, claim.Value, claim.ValueType, claim.Issuer));
        }

        return identity;
    }
}
