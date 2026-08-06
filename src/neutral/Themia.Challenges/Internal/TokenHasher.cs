using System.Security.Cryptography;
using System.Text;

namespace Themia.Challenges.Internal;

/// <summary>
/// Hashes an opaque token into the deterministic value stored in <c>token_hash</c> and looked up by
/// <see cref="IChallengeDialect.SelectLiveByTokenHashSql"/>.
/// </summary>
/// <remarks>
/// <b>Unsalted, and that is the point — do not "fix" it.</b> This hash is a lookup key: the caller
/// presents only a token and the store has to find the row, so the same token must always produce the
/// same value. A per-row salt would make that impossible, since choosing the salt requires already
/// knowing the row.
/// <para>
/// What makes an unsalted hash acceptable here and unacceptable for
/// <see cref="SecretHasher"/> is the size of the input space, not the algorithm.
/// <see cref="ChallengeFormatKind.OpaqueToken"/> secrets are
/// <see cref="RandomNumberGenerator"/> bytes — 32 of them by default, so 256 bits — and no rainbow
/// table or GPU walks that. A 6-digit numeric code has 10^6 candidates and would be recovered from an
/// unsalted digest instantly, which is exactly why the numeric path keeps the salted PBKDF2 in
/// <see cref="SecretHasher"/> and is never stored here.
/// </para>
/// <para>
/// A row for an opaque-token challenge therefore carries <b>both</b>: this value, to find it, and the
/// salted <see cref="SecretHasher"/> hash, to accept it. The lookup narrows; the constant-time compare
/// decides.
/// </para>
/// </remarks>
internal static class TokenHasher
{
    /// <summary>Returns the Base64 SHA-256 of <paramref name="token"/>.</summary>
    /// <param name="token">The plaintext token. Never persisted or logged by this method.</param>
    /// <returns>The Base64-encoded digest, safe to persist as a plain text column.</returns>
    public static string Hash(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
