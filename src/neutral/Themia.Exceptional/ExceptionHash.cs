using System.Security.Cryptography;
using System.Text;

namespace Themia.Exceptional;

/// <summary>Computes the deterministic, process-stable rollup hash for an exception.</summary>
public static class ExceptionHash
{
    /// <summary>
    /// Returns a 64-char hex SHA-256 of type + signature. SHA-256 (not <see cref="string.GetHashCode()"/>,
    /// which is per-process randomized) so the same error rolls up across app restarts.
    /// </summary>
    public static string Compute(string type, string? signature)
    {
        var input = $"{type}\n{signature}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
