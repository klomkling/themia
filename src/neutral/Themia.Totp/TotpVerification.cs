namespace Themia.Totp;

/// <summary>The result of verifying a submitted code.</summary>
/// <param name="Outcome">Why it succeeded or failed.</param>
/// <param name="MatchedStep">
/// The step the code matched, or <c>-1</c> when it matched none. Exposed so a caller can record it —
/// for example to reject codes older than the one just used.
/// </param>
public sealed record TotpVerification(TotpOutcome Outcome, long MatchedStep)
{
    /// <summary>Whether the code was accepted.</summary>
    public bool Succeeded => Outcome == TotpOutcome.Valid;
}
