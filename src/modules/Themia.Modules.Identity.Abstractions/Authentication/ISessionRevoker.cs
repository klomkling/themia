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
    /// <see cref="SessionClaims.GetSessionId(ICurrentUser)"/>). A null, blank or malformed value throws. A well-formed
    /// id that matches none of the user's sessions keeps nothing and revokes every session, the caller's included.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of refresh tokens revoked.</returns>
    /// <exception cref="ArgumentException"><paramref name="keepSessionId"/> is null, blank or not a well-formed session id.</exception>
    Task<int> RevokeAllForUserExceptAsync(Guid userId, string keepSessionId, CancellationToken cancellationToken = default);
}
