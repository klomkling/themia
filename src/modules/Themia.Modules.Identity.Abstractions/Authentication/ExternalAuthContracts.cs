namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>A headless external-login request: the authorization code the client obtained, plus the
/// redirect URI it used, an optional PKCE verifier, and an optional nonce.</summary>
/// <param name="Code">The authorization code obtained by the client.</param>
/// <param name="RedirectUri">The redirect URI the client used in the authorization request.</param>
/// <param name="CodeVerifier">An optional PKCE code verifier.</param>
/// <param name="Nonce">An optional nonce. When supplied, the provider requires the id_token's
/// <c>nonce</c> claim to be present and equal to this value; when omitted, no nonce check runs.</param>
public readonly record struct ExternalAuthRequest(
    string Code, string RedirectUri, string? CodeVerifier = null, string? Nonce = null);

/// <summary>A provider identity normalized to a common shape.</summary>
/// <param name="Provider">The provider key (lowercased).</param>
/// <param name="Subject">The stable provider subject (sub).</param>
/// <param name="Email">The email, if the provider returned one.</param>
/// <param name="EmailVerified">Whether the provider asserts the email is verified.</param>
/// <param name="DisplayName">An optional display name.</param>
public readonly record struct ExternalIdentity(
    string Provider, string Subject, string? Email, bool EmailVerified, string? DisplayName)
{
    /// <summary>The second factor the provider proved, set only by a provider that was configured to trust the identity provider's
    /// multi-factor claims and only after those claims passed verification (trusted issuer, <c>mfa</c> plus a strong method, fresh
    /// <c>auth_time</c>). Null means the provider proved no second factor, which is the case for every provider by default.
    /// A custom <see cref="IExternalAuthProvider"/> must set it only after verifying those claims itself (for example with
    /// <c>FederatedMfaVerifier</c> reporting <c>Satisfied</c>): the login flow trusts a non-null value as a strong, fresh proof and skips the
    /// local second factor.</summary>
    public ExternalSecondFactorProof? VerifiedSecondFactor { get; init; }
}

/// <summary>A second factor an external identity provider proved and Themia verified.</summary>
/// <param name="Amr">The known authentication-method references that were verified (for example <c>otp</c>, <c>mfa</c>).</param>
/// <param name="AuthenticatedAt">When the user authenticated at the provider (the id_token <c>auth_time</c>).</param>
public sealed record ExternalSecondFactorProof(IReadOnlyList<string> Amr, DateTimeOffset AuthenticatedAt);

/// <summary>The outcome of a provider exchange. Expected failures are typed (not exceptions);
/// genuine faults (network/5xx) throw.</summary>
public readonly record struct ExternalAuthResult
{
    private ExternalAuthResult(bool ok, ExternalIdentity? identity, string? failureReason)
    {
        Succeeded = ok; Identity = identity; FailureReason = failureReason;
    }

    /// <summary>Whether the exchange + validation succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>The normalized identity on success; otherwise null.</summary>
    public ExternalIdentity? Identity { get; }

    /// <summary>An internal failure reason (audit only) on failure; otherwise null.</summary>
    public string? FailureReason { get; }

    /// <summary>Creates a success result.</summary>
    /// <param name="identity">The normalized provider identity.</param>
    public static ExternalAuthResult Success(ExternalIdentity identity) => new(true, identity, null);

    /// <summary>Creates a failure result.</summary>
    /// <param name="reason">An internal failure reason for audit.</param>
    public static ExternalAuthResult Failed(string reason) => new(false, null, reason);
}

/// <summary>A pluggable external-identity provider. Implementations perform the server-side code
/// exchange and validate the resulting identity. Registered by <see cref="Name"/>.</summary>
public interface IExternalAuthProvider
{
    /// <summary>The provider key (lowercased; matches the {provider} route segment, case-insensitive).</summary>
    string Name { get; }

    /// <summary>Exchanges the authorization code for the provider identity.</summary>
    /// <param name="request">The external-login request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The exchange result.</returns>
    Task<ExternalAuthResult> ExchangeAsync(ExternalAuthRequest request, CancellationToken cancellationToken = default);
}
