namespace Themia.Challenges;

/// <summary>The shape of secret a <see cref="ChallengeFormat"/> describes.</summary>
public enum ChallengeFormatKind
{
    /// <summary>A numeric code of a fixed digit count — e.g. a 6-digit phone/email OTP.</summary>
    Numeric,

    /// <summary>A cryptographically random opaque token of a fixed byte length — e.g. a magic-link or
    /// email-verification token.</summary>
    OpaqueToken,
}

/// <summary>
/// Describes the shape of the secret a challenge issues: what kind of secret, and how long it is. A
/// sealed class with static factories rather than an enum, because the length varies independently of
/// the kind (a 4-digit PIN and a 6-digit OTP are both <see cref="ChallengeFormatKind.Numeric"/>).
/// </summary>
public sealed class ChallengeFormat
{
    private ChallengeFormat(ChallengeFormatKind kind, int length)
    {
        Kind = kind;
        Length = length;
    }

    /// <summary>The kind of secret this format describes.</summary>
    public ChallengeFormatKind Kind { get; }

    /// <summary>
    /// For <see cref="ChallengeFormatKind.Numeric"/>, the digit count. For
    /// <see cref="ChallengeFormatKind.OpaqueToken"/>, the number of random bytes backing the token
    /// (before encoding), not the encoded string length.
    /// </summary>
    public int Length { get; }

    /// <summary>Creates a numeric-code format, e.g. a 6-digit phone/email OTP.</summary>
    /// <param name="length">The digit count. Must be positive.</param>
    /// <returns>A <see cref="ChallengeFormatKind.Numeric"/> format of the given length.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is zero or negative.</exception>
    public static ChallengeFormat Numeric(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        return new ChallengeFormat(ChallengeFormatKind.Numeric, length);
    }

    /// <summary>Creates an opaque-token format, e.g. a magic-link or email-verification token.</summary>
    /// <param name="bytes">The number of random bytes backing the token. Must be positive.</param>
    /// <returns>A <see cref="ChallengeFormatKind.OpaqueToken"/> format of the given byte length.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is zero or negative.</exception>
    public static ChallengeFormat OpaqueToken(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        return new ChallengeFormat(ChallengeFormatKind.OpaqueToken, bytes);
    }
}
