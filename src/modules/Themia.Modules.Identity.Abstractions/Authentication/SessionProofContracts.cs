using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The second-factor proof of one session as stored on the server.</summary>
/// <param name="Amr">The authentication-method references, space separated (for example <c>otp mfa</c>).</param>
/// <param name="Strength">How much the second factor proves.</param>
/// <param name="AuthenticatedAt">When the second factor was verified. The access token <c>auth_time</c> is this value and refresh never moves it.</param>
/// <param name="Restriction">What the session may do until it enrols a strong method.</param>
public sealed record SessionProofSnapshot(
    string Amr,
    SessionProofStrength Strength,
    DateTimeOffset AuthenticatedAt,
    SessionRestriction Restriction = SessionRestriction.None);

/// <summary>Stores the second-factor proof of a session, keyed by the session (refresh-token family) id. A session with no row has no
/// second factor, so a missing proof always reads as "none". Every method resolves the owning user in scope first.</summary>
public interface ISessionProofStore
{
    /// <summary>Reads the proof of a live session of <paramref name="userId"/>, or null when the session is not live, belongs to
    /// someone else, or has no proof.</summary>
    /// <param name="userId">The session owner.</param>
    /// <param name="sessionId">The session id (the <c>sid</c> claim).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<SessionProofSnapshot?> GetAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Writes (or replaces) the proof of a live session of <paramref name="userId"/>.</summary>
    /// <param name="userId">The session owner.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="proof">The proof.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>False when the session is not a live session of this user and nothing was written.</returns>
    Task<bool> SaveAsync(Guid userId, Guid sessionId, SessionProofSnapshot proof, CancellationToken cancellationToken = default);

    /// <summary>Lifts the restriction of a session with one update by session id. Only the second-factor bridge calls this, after a strong
    /// enrolment succeeded.</summary>
    /// <param name="userId">The session owner.</param>
    /// <param name="sessionId">The session id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>True when a restricted session was lifted.</returns>
    Task<bool> LiftRestrictionAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);
}
