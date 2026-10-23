namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>Ends a user's other sessions while keeping one. Kept apart from <see cref="IRefreshTokenService"/> so
/// adding it does not break that interface's implementers.</summary>
public interface ISessionRevoker
{
    /// <summary>Revokes the user's active refresh tokens in every session except the one named, so a self-service
    /// password change can sign out the other devices and keep the caller signed in. Resolves the user in the ambient
    /// scope like <see cref="IRefreshTokenService.RevokeAllForUserAsync"/>; a user outside it revokes nothing.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="keepSessionId">The session to keep: the caller's <c>sid</c> claim (see
    /// <see cref="SessionClaims.GetSessionId(ICurrentUser)"/>). The call throws, and revokes nothing, unless this
    /// names an active session of this user: a null, blank or malformed value, an unknown id, another user's session, or
    /// one that is revoked or expired. A caller that catches it should fall back to revoking every session and issuing
    /// a fresh pair.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of refresh tokens revoked.</returns>
    /// <exception cref="ArgumentException"><paramref name="keepSessionId"/> does not name an active session of the user.</exception>
    Task<int> RevokeAllForUserExceptAsync(Guid userId, string keepSessionId, CancellationToken cancellationToken = default);
}
