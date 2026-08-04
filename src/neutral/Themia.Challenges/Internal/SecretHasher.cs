using System.Security.Cryptography;
using System.Text;

namespace Themia.Challenges.Internal;

/// <summary>
/// Hashes and verifies a challenge secret with a per-call random salt.
/// </summary>
/// <remarks>
/// <para>
/// Read this before relying on the hash for more than it provides. Hashing a 6-digit OTP is
/// <b>not</b> the same problem as hashing a password: the entire input space is 10^<c>length</c>
/// candidates, and a GPU walks that space — salted or not — in well under a second once it has
/// the stored hash and salt. No iteration count that stays usable on a synchronous login path
/// changes that math meaningfully. What hashing here actually buys is protection against
/// <i>casual</i> disclosure: a support engineer scrolling the challenges table, a code sitting in
/// a query log or APM trace, a screenshot of a debugger. It is not a defense against an attacker
/// who has exfiltrated the row and is willing to spend a second brute-forcing it.
/// </para>
/// <para>
/// The property that actually makes a leaked row worthless is <b>short TTL plus single-use</b>:
/// by the time an attacker has the hash, salt, and enough compute to invert it, the challenge has
/// either already been consumed or has expired. Do not relax the TTL on the theory that the hash
/// is carrying the security weight here — it isn't.
/// </para>
/// </remarks>
internal static class SecretHasher
{
    // PBKDF2-HMAC-SHA256. Rfc2898DeriveBytes.Pbkdf2 is available on both net8.0 and net10.0,
    // unlike newer BCL KDFs. A bare SHA256 was rejected in favor of a standard, tunable KDF, but
    // the iteration count is kept low (not the ~600k OWASP recommends for password storage):
    // verification runs synchronously on a user-facing login path, and — per the remarks above —
    // a 6-digit secret's tiny keyspace means a large iteration count buys negligible additional
    // resistance while adding real latency to every single verification attempt. The TTL and
    // single-use semantics are what actually protect a leaked row, not this number.
    private const int Iterations = 10_000;
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;

    /// <summary>Hashes <paramref name="secret"/> under a freshly drawn random salt.</summary>
    /// <param name="secret">The plaintext secret to hash. Never persisted or logged by this method.</param>
    /// <returns>The Base64-encoded hash and the Base64-encoded salt used to produce it — both
    /// safe to persist as plain text columns.</returns>
    public static (string Hash, string Salt) Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = DeriveHash(secret, salt);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    /// <summary>Verifies <paramref name="secret"/> against a previously stored hash and salt.</summary>
    /// <param name="secret">The plaintext secret supplied by the caller attempting to verify.</param>
    /// <param name="hash">The Base64-encoded hash produced by a prior call to <see cref="Hash"/>.</param>
    /// <param name="salt">The Base64-encoded salt produced by the same call to <see cref="Hash"/>.</param>
    /// <returns><see langword="true"/> if <paramref name="secret"/> matches; otherwise <see langword="false"/>.</returns>
    public static bool Verify(string secret, string hash, string salt)
    {
        var expected = Convert.FromBase64String(hash);
        var actual = DeriveHash(secret, Convert.FromBase64String(salt));

        // Constant-time comparison: a length- or content-dependent early return here would leak
        // timing information about the hash, undermining the point of hashing in the first place.
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] DeriveHash(string secret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);
}
