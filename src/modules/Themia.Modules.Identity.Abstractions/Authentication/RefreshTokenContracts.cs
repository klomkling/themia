using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The outcome of validating + rotating a refresh token.</summary>
public enum RefreshOutcome
{
    /// <summary>Rotated; a successor was issued.</summary>
    Success,

    /// <summary>Unknown, expired, or owner not in scope.</summary>
    Invalid,

    /// <summary>A consumed/revoked token was replayed; the family was revoked.</summary>
    ReuseDetected,
}

/// <summary>A newly issued refresh token. The raw value is returned exactly once.</summary>
/// <param name="RawToken">The opaque raw token (never persisted).</param>
/// <param name="ExpiresAt">Absolute expiry.</param>
/// <param name="FamilyId">The rotation family.</param>
public readonly record struct RefreshIssue(string RawToken, DateTimeOffset ExpiresAt, Guid FamilyId);

/// <summary>The result of <see cref="IRefreshTokenService.ValidateAndRotateAsync"/>.</summary>
public readonly record struct RefreshValidationResult
{
    private RefreshValidationResult(RefreshOutcome outcome, User? user, RefreshIssue? replacement)
    {
        Outcome = outcome;
        User = user;
        Replacement = replacement;
    }

    /// <summary>The outcome.</summary>
    public RefreshOutcome Outcome { get; }

    /// <summary>The resolved owning user on success; otherwise null.</summary>
    public User? User { get; }

    /// <summary>The successor refresh token on success; otherwise null.</summary>
    public RefreshIssue? Replacement { get; }

    /// <summary>Gets the resolved user and successor token when the rotation succeeded.</summary>
    /// <param name="user">The resolved owning user, when this returns <see langword="true"/>.</param>
    /// <param name="replacement">The successor refresh token, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> if the rotation succeeded.</returns>
    public bool TryGetSuccess([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out User? user, out RefreshIssue replacement)
    {
        user = User;
        replacement = Replacement ?? default;
        return Outcome == RefreshOutcome.Success;
    }

    /// <summary>Creates a success result.</summary>
    public static RefreshValidationResult Success(User user, RefreshIssue replacement) =>
        new(RefreshOutcome.Success, user, replacement);

    /// <summary>Creates an invalid result.</summary>
    public static RefreshValidationResult Invalid() => new(RefreshOutcome.Invalid, null, null);

    /// <summary>Creates a reuse-detected result.</summary>
    public static RefreshValidationResult ReuseDetected() => new(RefreshOutcome.ReuseDetected, null, null);
}

/// <summary>Issues, rotates, and revokes refresh tokens. All operations resolve the owning user in the
/// ambient tenant (else genuine platform) scope before acting on a token, so cross-tenant tokens can
/// never be rotated, revoked, or accepted.</summary>
public interface IRefreshTokenService
{
    /// <summary>Issues a new refresh token for a user, always starting a new rotation family.</summary>
    /// <param name="userId">The owning user id (must resolve in scope).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The issued token (raw value returned once).</returns>
    Task<RefreshIssue> IssueAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Validates a presented raw token and, on success, consumes it and issues a successor in
    /// the same family. A replayed consumed/revoked token revokes the entire family.</summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The validation result.</returns>
    Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes the presented token's family, or all non-expired tokens for its owner.</summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="allForUser">When true, revoke every non-expired token for the owner.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default);

    /// <summary>Revokes every active refresh token a user holds, without needing one of them in hand.</summary>
    /// <param name="userId">The owning user (must resolve in scope).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>How many tokens were revoked; 0 when the user does not resolve in scope.</returns>
    /// <remarks>
    /// For the moments a caller has no token to present — after unlinking a compromised external identity,
    /// on an administrator's "sign out everywhere". Sessions are not tagged by the identity that opened
    /// them, so this is the only way to end the one a compromised identity holds.
    /// <para>
    /// No default implementation, unlike <see cref="ResolveOwnerAsync"/>. A default that did nothing would
    /// let a caller believe every session had ended while all of them stayed valid; a custom implementation
    /// that cannot revoke should fail to compile rather than fail silently.
    /// </para>
    /// </remarks>
    Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Resolves the id of the user who owns a presented raw token, without consuming, rotating,
    /// or revoking it. Used to attribute an audit event (e.g. logout) to a user before the token is
    /// acted upon.</summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The owning user's id, or <see langword="null"/> when the token does not resolve to a
    /// user in scope.</returns>
    /// <remarks>Default implementation returns <see langword="null"/>, so an existing custom
    /// <see cref="IRefreshTokenService"/> implementation keeps compiling without change — it simply
    /// does not attribute a user id to <see cref="LogoutContext"/> until it implements this method.</remarks>
    Task<Guid?> ResolveOwnerAsync(string rawToken, CancellationToken cancellationToken = default) =>
        Task.FromResult<Guid?>(null);
}
