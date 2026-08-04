using System.Security.Cryptography;

namespace Themia.Challenges.Internal;

/// <summary>
/// Draws the random secret behind a challenge — a numeric code or an opaque token — from a
/// cryptographically secure source. Internal: callers only ever see the rendered secret, never
/// this generator.
/// </summary>
internal static class SecretGenerator
{
    /// <summary>Generates a new secret matching the given <see cref="ChallengeFormat"/>.</summary>
    /// <param name="format">The shape of secret to produce.</param>
    /// <returns>The rendered secret — digits for <see cref="ChallengeFormatKind.Numeric"/>,
    /// a Base64Url token for <see cref="ChallengeFormatKind.OpaqueToken"/>.</returns>
    /// <exception cref="NotSupportedException">
    /// <paramref name="format"/>'s <see cref="ChallengeFormat.Kind"/> has no generator yet.
    /// </exception>
    public static string Generate(ChallengeFormat format) => format.Kind switch
    {
        ChallengeFormatKind.Numeric => GenerateNumeric(format.Length),
        ChallengeFormatKind.OpaqueToken => GenerateOpaqueToken(format.Length),
        _ => throw new NotSupportedException($"No secret generator registered for format kind '{format.Kind}'."),
    };

    // RandomNumberGenerator, never Random: Random is a seeded PRNG whose output is predictable
    // once a handful of draws are observed, which would make a login code guessable. Each digit
    // is drawn independently and written into a char buffer — never through int.ToString(), which
    // would silently drop leading zeros (e.g. "004821" -> "4821") and break comparison against
    // what the user actually typed.
    private static string GenerateNumeric(int length)
    {
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }

        return new string(buffer);
    }

    private static string GenerateOpaqueToken(int byteLength)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteLength);
        return ToBase64Url(bytes);
    }

    // System.Buffers.Text.Base64Url isn't available on net8.0, so the URL-safe alphabet is
    // produced manually from the standard Base64 encoding (unpadded, '+'/'/' remapped).
    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
