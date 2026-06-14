using System.Security.Cryptography;
using System.Text;

namespace Themia.Modules.Identity.Hashing;

/// <summary>Hashes opaque tokens (SHA-256) for at-rest storage, with a constant-time compare. Tokens carry their own entropy, so no salt is needed.</summary>
internal static class TokenHasher
{
    /// <summary>Returns the Base64 SHA-256 hash of a raw token.</summary>
    public static string Hash(string rawToken)
    {
        ArgumentNullException.ThrowIfNull(rawToken);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Constant-time check that a stored hash matches a presented raw token. Retained as a hashing
    /// utility (and exercised by tests); production token consume now matches by an exact <c>token_hash</c>
    /// DB lookup rather than loading and comparing in memory.</summary>
    public static bool Matches(string storedHash, string rawToken)
    {
        ArgumentNullException.ThrowIfNull(storedHash);
        ArgumentNullException.ThrowIfNull(rawToken);

        var presented = Encoding.UTF8.GetBytes(Hash(rawToken));
        var stored = Encoding.UTF8.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(presented, stored);
    }
}
