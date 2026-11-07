using System.Security.Cryptography;
using System.Text;

namespace Themia.SecondFactor;

/// <summary>Generates human-typeable recovery codes ("ABCD-EFGH-JKMN").</summary>
public static class RecoveryCodeGenerator
{
    /// <summary>Codes minted per batch.</summary>
    public const int DefaultCount = 10;

    // Crockford-style alphabet without 0/O/1/I/L so a code read from paper is unambiguous.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int GroupSize = 4;
    private const int Groups = 3;

    /// <summary>Generates <paramref name="count"/> distinct codes (≈ 59 bits each).</summary>
    public static IReadOnlyList<string> Generate(int count = DefaultCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var codes = new HashSet<string>(StringComparer.Ordinal);
        while (codes.Count < count)
        {
            codes.Add(One());
        }

        return [.. codes];
    }

    private static string One()
    {
        var chars = new char[(GroupSize * Groups) + (Groups - 1)];
        var index = 0;
        for (var group = 0; group < Groups; group++)
        {
            if (group > 0)
            {
                chars[index++] = '-';
            }

            for (var i = 0; i < GroupSize; i++)
            {
                chars[index++] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        }

        return new string(chars);
    }
}

/// <summary>
/// Keyed HMAC-SHA256 over <c>userId:code</c>. Deterministic, so a stored hash can be looked up by value, and bound to
/// the user so a hash copied to another account never verifies.
/// </summary>
public sealed class RecoveryCodeHasher
{
    /// <summary>The value stored in <c>hash_scheme</c> for hashes produced here.</summary>
    public const string Scheme = "hmac-sha256-v1";

    private const int MinimumKeyBytes = 32;

    private readonly byte[] key;

    /// <summary>Creates the hasher with a server-side key of at least 32 bytes.</summary>
    public RecoveryCodeHasher(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < MinimumKeyBytes)
        {
            throw new ArgumentException($"The recovery-code key must be at least {MinimumKeyBytes} bytes.", nameof(key));
        }

        this.key = (byte[])key.Clone();
    }

    /// <summary>Hashes <paramref name="code"/> for <paramref name="userId"/>; case and dashes are ignored.</summary>
    public string Hash(string userId, string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var normalized = code.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{userId}:{normalized}"));
        return Convert.ToHexString(mac);
    }
}
