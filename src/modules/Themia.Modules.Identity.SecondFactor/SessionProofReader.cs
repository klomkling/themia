using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor;

/// <summary>Reads the second-factor proof of a session from the server, as the <see cref="MfaProof"/> the second-factor service takes.
/// Never build the proof from token claims: they only describe the proof at the time the token was issued.</summary>
public interface ISessionProofReader
{
    /// <summary>Reads the proof of a live session of <paramref name="userId"/>.</summary>
    /// <param name="userId">The session owner.</param>
    /// <param name="sessionId">The session id (the <c>sid</c> claim).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The proof, or null when the session is not live, is not this user's, or has no second factor. A restricted session
    /// (opened by a recovery code, or one that must enrol a stronger method) reads as <see cref="MfaProof.RecoveryCodeOnly"/>.</returns>
    Task<MfaProof?> GetAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default);
}

internal sealed class SessionProofReader(ISessionProofStore store) : ISessionProofReader
{
    public async Task<MfaProof?> GetAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await store.GetAsync(userId, sessionId, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : ToProof(snapshot);
    }

    internal static MfaProof ToProof(SessionProofSnapshot snapshot) => new(
        snapshot.Strength == SessionProofStrength.Strong ? MfaStrength.Strong : MfaStrength.Weak,
        snapshot.AuthenticatedAt,
        RecoveryCodeOnly: snapshot.Restriction != SessionRestriction.None);

    internal static SessionProofSnapshot ToSnapshot(SecondFactorResult verified, SessionRestriction restriction) => new(
        string.Join(' ', verified.Amr),
        // The service reports a recovery code as Strong, but it must never read as a strong proof: it would pass the fresh-strong
        // gate and let a restricted session remove a method or mint new codes.
        verified.Strength == MfaStrength.Strong && !verified.UsedRecoveryCode ? SessionProofStrength.Strong : SessionProofStrength.Weak,
        verified.AuthenticatedAt ?? throw new InvalidOperationException("Only a succeeded second-factor result carries a proof."),
        restriction);
}
