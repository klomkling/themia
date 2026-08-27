namespace Themia.WebAuthn;

/// <summary>Why a ceremony succeeded or failed.</summary>
/// <remarks>
/// An enum rather than a boolean: a caller that cannot tell a stale challenge from a cloned credential
/// cannot respond to them differently, and one of those is a security event worth alerting on.
/// </remarks>
public enum WebAuthnOutcome
{
    /// <summary>The response verified.</summary>
    Valid,

    /// <summary>
    /// No open ceremony matched the challenge — unknown, expired, or already used. A replayed response
    /// lands here.
    /// </summary>
    ChallengeNotFound,

    /// <summary>
    /// The response verified, but its signature counter did not move forward: two authenticators are
    /// answering for one credential. Worth alerting on — see <see cref="SignCounterPolicy"/>.
    /// </summary>
    SignCounterRegressed,

    /// <summary>The library rejected the response: bad signature, wrong origin, wrong relying party.</summary>
    VerificationFailed,
}
