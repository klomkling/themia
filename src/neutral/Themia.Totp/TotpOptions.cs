namespace Themia.Totp;

/// <summary>Shape of the codes this package generates and accepts (RFC 6238 §4).</summary>
public sealed class TotpOptions
{
    /// <summary>Number of digits in a code. Defaults to 6, which is what authenticator apps display.</summary>
    public int Digits { get; set; } = 6;

    /// <summary>Length of a time step. Defaults to 30 seconds, the RFC's recommendation.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The HMAC hash. Defaults to <see cref="TotpAlgorithm.Sha1"/>.</summary>
    public TotpAlgorithm Algorithm { get; set; } = TotpAlgorithm.Sha1;

    /// <summary>
    /// How many steps either side of the current one a code is still accepted for. Defaults to 1,
    /// tolerating roughly ±30 seconds of clock skew between the authenticator and this host.
    /// </summary>
    /// <remarks>
    /// A wider window is a wider replay window for anyone who observes a code, which is what
    /// <see cref="ITotpReplayStore"/> closes. Raising it without a replay store is the failure this
    /// package is built to prevent.
    /// </remarks>
    public int VerificationWindowSteps { get; set; } = 1;

    /// <summary>The smallest decoded secret this package will verify against, in bytes (RFC 4226 §4).</summary>
    /// <remarks>
    /// Matches the floor <see cref="ITotpService.GenerateSecret"/> refuses to mint below. Verifying a
    /// shorter key than the package will issue is the asymmetry worth closing: base32 ignores padding,
    /// so a secret that decodes to nothing at all — <c>"========"</c>, or whatever a broken decrypt
    /// leaves behind — otherwise HMACs happily against an empty key and produces codes anyone can
    /// reproduce.
    /// </remarks>
    internal const int MinimumSecretBytes = 16;

    /// <summary>Reports the first configuration problem, or null when the options are usable.</summary>
    /// <remarks>
    /// Shared by <see cref="TotpService"/>'s constructor and the <c>ValidateOnStart</c> registration, so
    /// a bad value is refused at boot rather than on somebody's first login.
    /// </remarks>
    internal string? Validate()
    {
        if (Digits is < 6 or > 10)
        {
            return $"Digits must be between 6 and 10, but was {Digits}.";
        }

        // Not just "> TimeSpan.Zero": the step is computed as unix seconds / (long)Period.TotalSeconds,
        // so a sub-second period truncates to a divisor of zero and every call throws
        // DivideByZeroException, while CreateProvisioningUri emits period=0. A fractional period
        // truncates silently to a different window than the one configured.
        if (Period < TimeSpan.FromSeconds(1) || Period.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            return $"Period must be a whole number of seconds and at least one, but was {Period}.";
        }

        if (VerificationWindowSteps < 0)
        {
            return $"VerificationWindowSteps cannot be negative, but was {VerificationWindowSteps}.";
        }

        return null;
    }
}
