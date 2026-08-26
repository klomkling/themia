namespace Themia.Totp;

/// <summary>The HMAC hash a TOTP code is derived with (RFC 6238 §1.2).</summary>
/// <remarks>
/// <see cref="Sha1"/> is the default because it is what authenticator applications overwhelmingly
/// implement; the other two exist so the package can be pinned against RFC 6238's full vector table,
/// and because a caller whose verifier is not a consumer app may prefer them.
/// </remarks>
public enum TotpAlgorithm
{
    /// <summary>HMAC-SHA-1. The default, and the only algorithm most authenticator apps accept.</summary>
    Sha1,

    /// <summary>HMAC-SHA-256.</summary>
    Sha256,

    /// <summary>HMAC-SHA-512.</summary>
    Sha512,
}
