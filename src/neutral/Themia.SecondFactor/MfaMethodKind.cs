namespace Themia.SecondFactor;

/// <summary>A second-factor method a user can hold. Recovery codes are a separate backup, not a kind.</summary>
public enum MfaMethodKind
{
    /// <summary>A WebAuthn passkey (strong).</summary>
    Passkey,

    /// <summary>An authenticator-app TOTP secret (strong).</summary>
    Totp,

    /// <summary>A one-time code sent by email (weak).</summary>
    EmailOtp,

    /// <summary>A one-time code sent by SMS (weak).</summary>
    SmsOtp,
}

/// <summary>How much a method proves. Sensitive actions require <see cref="Strong"/>.</summary>
public enum MfaStrength
{
    /// <summary>Possession of a delivery channel (email, SMS) — interceptable.</summary>
    Weak,

    /// <summary>Possession of a key or secret bound to the user (passkey, TOTP, recovery code).</summary>
    Strong,
}

/// <summary>Strength lookup for <see cref="MfaMethodKind"/>.</summary>
public static class MfaMethodKindExtensions
{
    /// <summary>The strength of <paramref name="kind"/>.</summary>
    public static MfaStrength Strength(this MfaMethodKind kind) => kind switch
    {
        MfaMethodKind.Passkey => MfaStrength.Strong,
        MfaMethodKind.Totp => MfaStrength.Strong,
        MfaMethodKind.EmailOtp => MfaStrength.Weak,
        MfaMethodKind.SmsOtp => MfaStrength.Weak,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown second-factor method kind."),
    };
}
