using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.SecondFactor;

namespace Themia.Modules.Identity.SecondFactor;

/// <summary>Builds the <see cref="SessionProofSnapshot"/> of a second factor that Themia verified, for
/// <see cref="IProofAuthTokenIssuer.CompleteSessionAsync(Guid, string, SecondFactorStage, SessionRequest, SessionProofSnapshot, CancellationToken)"/>.</summary>
public static class SessionProofs
{
    /// <summary>Maps a succeeded verification to a session proof. A recovery code is never strong, whatever the service reports.</summary>
    /// <param name="verified">The succeeded result of the verification.</param>
    /// <param name="restriction">What the new session may do; <see cref="SessionRestriction.RecoveryOnly"/> for a recovery code.</param>
    /// <returns>The proof to store on the session.</returns>
    /// <exception cref="InvalidOperationException">The result carries no time, so it is not a succeeded verification.</exception>
    public static SessionProofSnapshot From(SecondFactorResult verified, SessionRestriction restriction)
    {
        ArgumentNullException.ThrowIfNull(verified);
        return new SessionProofSnapshot(
            string.Join(' ', verified.Amr),
            // The service reports a recovery code as Strong, but it must never read as a strong proof: it would pass the fresh-strong
            // gate and let a restricted session remove a method or mint new codes.
            verified.Strength == MfaStrength.Strong && !verified.UsedRecoveryCode ? SessionProofStrength.Strong : SessionProofStrength.Weak,
            verified.AuthenticatedAt ?? throw new InvalidOperationException("Only a succeeded second-factor result carries a proof."),
            restriction);
    }
}
