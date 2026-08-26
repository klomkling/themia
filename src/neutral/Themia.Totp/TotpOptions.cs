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
}
