using System.Collections.Generic;

namespace Themia.Modules.Identity.AspNetCore.External;

/// <summary>Immutable configuration for an OIDC-style external-auth provider. Holds the token-endpoint
/// exchange parameters, the expected token issuer/audience, the signing-key strategy
/// (JWKS for RS256, <em>or</em> a symmetric secret for HS256), claim-name overrides, and provider
/// quirks (e.g. LINE never returns <c>email_verified</c>, so the email is treated as verified).</summary>
/// <remarks>The signing-key strategy is exactly one of <see cref="JwksUri"/> (asymmetric) or
/// <see cref="SymmetricSecret"/> (symmetric). Supplying neither, or both, is a configuration error
/// detected by <see cref="OidcExternalAuthProvider"/>.</remarks>
public sealed class OidcProviderConfig
{
    /// <summary>The provider key (lowercased; matches the route segment, case-insensitive).</summary>
    public required string Name { get; init; }

    /// <summary>The OAuth token endpoint that the authorization code is exchanged at.</summary>
    public required Uri TokenEndpoint { get; init; }

    /// <summary>The OAuth client id.</summary>
    public required string ClientId { get; init; }

    /// <summary>The OAuth client secret.</summary>
    public required string ClientSecret { get; init; }

    /// <summary>The OAuth scopes the provider was authorized with (informational; the code exchange
    /// does not resend scopes, but adopters may inspect this).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = Array.Empty<string>();

    /// <summary>The expected <c>iss</c> of the returned id_token.</summary>
    public required string Issuer { get; init; }

    /// <summary>The expected <c>aud</c> of the returned id_token (the OAuth client id).</summary>
    public required string Audience { get; init; }

    /// <summary>The JWKS endpoint for RS256 (asymmetric) validation. Mutually exclusive with
    /// <see cref="SymmetricSecret"/>.</summary>
    public Uri? JwksUri { get; init; }

    /// <summary>The shared secret for HS256 (symmetric) validation (e.g. LINE's channel secret).
    /// Mutually exclusive with <see cref="JwksUri"/>.</summary>
    public string? SymmetricSecret { get; init; }

    /// <summary>The claim name carrying the stable subject. Defaults to <c>sub</c>.</summary>
    public string SubjectClaim { get; init; } = "sub";

    /// <summary>The claim name carrying the email. Defaults to <c>email</c>.</summary>
    public string EmailClaim { get; init; } = "email";

    /// <summary>The claim name carrying the email-verified flag. Defaults to <c>email_verified</c>.</summary>
    public string EmailVerifiedClaim { get; init; } = "email_verified";

    /// <summary>The claim name carrying the display name. Defaults to <c>name</c>.</summary>
    public string NameClaim { get; init; } = "name";

    /// <summary>When <see langword="true"/>, the resulting email is always treated as verified,
    /// regardless of any <see cref="EmailVerifiedClaim"/> (providers like LINE never emit one).</summary>
    public bool EmailAlwaysVerified { get; init; }
}
