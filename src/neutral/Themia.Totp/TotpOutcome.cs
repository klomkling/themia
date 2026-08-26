namespace Themia.Totp;

/// <summary>Why a verification succeeded or failed.</summary>
/// <remarks>
/// An enum rather than a pair of booleans: a caller that cannot tell a wrong code from a replayed one
/// cannot log or rate-limit them differently, and adding a state later must break every exhaustive
/// consumer rather than compile silently.
/// </remarks>
public enum TotpOutcome
{
    /// <summary>The code matched a step inside the window and that step was not yet consumed.</summary>
    Valid,

    /// <summary>The code matched no step inside the window.</summary>
    InvalidCode,

    /// <summary>
    /// The code was correct, but its step had already been consumed — the replay this package exists
    /// to stop. Worth distinguishing: it means someone submitted a code that was genuinely issued.
    /// </summary>
    Replayed,
}
