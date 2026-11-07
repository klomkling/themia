namespace Themia.SecondFactor;

/// <summary>Authentication-method-reference values (RFC 8176 plus two Themia-specific ones) for federated assertions.</summary>
public static class AmrClaims
{
    /// <summary>Password.</summary>
    public const string Password = "pwd";

    /// <summary>Multiple-factor authentication was satisfied with a strong second factor.</summary>
    public const string Mfa = "mfa";

    /// <summary>One-time password from an authenticator app (TOTP) or a recovery code. Strong.</summary>
    public const string Otp = "otp";

    /// <summary>Software-secured key (a passkey). Strong.</summary>
    public const string SoftwareKey = "swk";

    /// <summary>Hardware-secured key. Strong.</summary>
    public const string HardwareKey = "hwk";

    /// <summary>One-time code by SMS. Weak.</summary>
    public const string Sms = "sms";

    /// <summary>One-time code by email (Themia-specific; RFC 8176 has no value). Weak.</summary>
    public const string EmailOtp = "email_otp";

    internal static readonly string[] StrongMethods = [Otp, SoftwareKey, HardwareKey];

    /// <summary>The amr values to emit after <paramref name="kind"/> passed as the second factor.
    /// <c>mfa</c> is emitted only for a strong method, so a weak-only result can never claim MFA.</summary>
    public static IReadOnlyList<string> ForSecondFactor(MfaMethodKind kind) => kind switch
    {
        MfaMethodKind.Passkey => [SoftwareKey, Mfa],
        MfaMethodKind.Totp => [Otp, Mfa],
        MfaMethodKind.SmsOtp => [Sms],
        MfaMethodKind.EmailOtp => [EmailOtp],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown second-factor method kind."),
    };

    /// <summary>The amr values to emit after a recovery code passed as the second factor (strong).</summary>
    public static IReadOnlyList<string> ForRecoveryCode() => [Otp, Mfa];
}

/// <summary>The claims a federated assertion carries that the verifier checks.</summary>
/// <param name="Issuer">The assertion issuer.</param>
/// <param name="Amr">The amr values.</param>
/// <param name="AuthTime">When the user last authenticated, or <see langword="null"/> if absent.</param>
public sealed record FederatedAssertion(string Issuer, IReadOnlyCollection<string> Amr, DateTimeOffset? AuthTime);

/// <summary>Why a federated assertion was or was not accepted as satisfying MFA.</summary>
public enum FederatedMfaOutcome
{
    /// <summary>Trusted issuer, MFA claimed, a strong method present, authentication fresh.</summary>
    Satisfied,

    /// <summary>The issuer is not in the trusted list.</summary>
    UntrustedIssuer,

    /// <summary>The assertion does not claim <c>mfa</c>.</summary>
    MissingMfa,

    /// <summary>MFA is claimed but only weak methods are listed.</summary>
    NoStrongMethod,

    /// <summary>No <c>auth_time</c> was supplied.</summary>
    MissingAuthTime,

    /// <summary>The authentication is older than allowed, or dated in the future beyond clock skew.</summary>
    StaleAuthTime,
}

/// <summary>Result helpers for <see cref="FederatedMfaOutcome"/>.</summary>
public static class FederatedMfaOutcomeExtensions
{
    /// <summary>Whether the assertion satisfies MFA.</summary>
    public static bool Succeeded(this FederatedMfaOutcome outcome) => outcome == FederatedMfaOutcome.Satisfied;
}

/// <summary>Decides whether a federated assertion carries a usable second factor. Pure, no I/O.</summary>
public static class FederatedMfaVerifier
{
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    /// <summary>Checks <paramref name="assertion"/> against the trusted issuers and freshness window.</summary>
    public static FederatedMfaOutcome Satisfies(
        FederatedAssertion assertion,
        IReadOnlyCollection<string> trustedIssuers,
        TimeSpan maxAge,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        ArgumentNullException.ThrowIfNull(trustedIssuers);

        if (!trustedIssuers.Contains(assertion.Issuer, StringComparer.Ordinal))
        {
            return FederatedMfaOutcome.UntrustedIssuer;
        }

        if (!assertion.Amr.Contains(AmrClaims.Mfa, StringComparer.Ordinal))
        {
            return FederatedMfaOutcome.MissingMfa;
        }

        if (!assertion.Amr.Any(value => AmrClaims.StrongMethods.Contains(value, StringComparer.Ordinal)))
        {
            return FederatedMfaOutcome.NoStrongMethod;
        }

        if (assertion.AuthTime is not { } authTime)
        {
            return FederatedMfaOutcome.MissingAuthTime;
        }

        return now - authTime > maxAge || authTime - now > ClockSkew
            ? FederatedMfaOutcome.StaleAuthTime
            : FederatedMfaOutcome.Satisfied;
    }
}
