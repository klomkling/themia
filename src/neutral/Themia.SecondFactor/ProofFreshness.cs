namespace Themia.SecondFactor;

/// <summary>The one definition of "a fresh strong proof", shared by every operation that demands it.</summary>
internal static class ProofFreshness
{
    internal static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Strong, not from a recovery code, and inside <paramref name="maxAge"/> (allowing a little clock skew into the future).</summary>
    internal static bool IsFreshStrong(MfaProof? proof, DateTimeOffset now, TimeSpan maxAge)
    {
        if (proof is not { Strength: MfaStrength.Strong, RecoveryCodeOnly: false } strong)
        {
            return false;
        }

        var age = now - strong.AuthenticatedAt;
        return age <= maxAge && age >= -ClockSkew;
    }
}
