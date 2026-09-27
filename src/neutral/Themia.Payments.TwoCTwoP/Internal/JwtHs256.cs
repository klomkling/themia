using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Themia.Payments.TwoCTwoP.Internal;

/// <summary>
/// HS256 JWT encode/decode for 2C2P's transport: every request and response body is
/// <c>{"payload": "&lt;JWT&gt;"}</c>, and the signature is the only authentication — there is no bearer or
/// basic header. No JWT library: this is base64url plus <see cref="HMACSHA256"/>, and a dependency here would
/// reach every consumer of a <c>net8.0;net10.0</c> package for thirty lines.
/// </summary>
internal static class JwtHs256
{
    /// <summary>Signs <paramref name="payload"/> as a compact HS256 JWT with header <c>{"alg":"HS256","typ":"JWT"}</c>.</summary>
    /// <param name="payload">The claims to encode. Values are written with the default
    /// <see cref="JsonSerializerOptions"/> — a <see cref="decimal"/> serializes as a JSON number with the
    /// scale it carries, culture-invariantly.</param>
    /// <param name="secret">The merchant secret key, used as the HMAC key's UTF-8 bytes.</param>
    /// <returns>The compact JWT: <c>header.payload.signature</c>.</returns>
    public static string Encode(IReadOnlyDictionary<string, object?> payload, string secret)
    {
        var header = Base64UrlEncode("""{"alg":"HS256","typ":"JWT"}"""u8.ToArray());
        var body = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{header}.{body}";
        var signature = Base64UrlEncode(Sign(signingInput, secret));

        return $"{signingInput}.{signature}";
    }

    /// <summary>
    /// Verifies the signature with <paramref name="secret"/> and, only if it matches, parses the payload
    /// segment as JSON. Never throws: a malformed token (wrong segment count, invalid base64url, non-JSON
    /// payload, a payload that isn't a JSON object) reports <c>false</c> the same as a bad signature.
    /// </summary>
    /// <param name="token">The compact JWT.</param>
    /// <param name="secret">The merchant secret key that must have signed it.</param>
    /// <param name="payload">The decoded payload, valid only when this returns <c>true</c>.</param>
    /// <returns>Whether the signature verified and the payload parsed as a JSON object.</returns>
    public static bool TryDecode(string token, string secret, out JsonElement payload)
    {
        payload = default;
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        byte[] payloadBytes;
        byte[] expectedSignature;
        byte[] actualSignature;
        try
        {
            expectedSignature = Sign($"{parts[0]}.{parts[1]}", secret);
            actualSignature = Base64UrlDecode(parts[2]);
            payloadBytes = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            payload = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] Sign(string signingInput, string secret) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signingInput));

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    /// <summary>Base64url-decodes a segment, returning the raw bytes.</summary>
    /// <exception cref="FormatException">The segment is not valid base64url.</exception>
    internal static byte[] Base64UrlDecode(string segment)
    {
        var base64 = segment.Replace('-', '+').Replace('_', '/');
        var padded = (base64.Length % 4) switch
        {
            2 => base64 + "==",
            3 => base64 + "=",
            0 => base64,
            _ => throw new FormatException("Invalid base64url segment length."),
        };

        return Convert.FromBase64String(padded);
    }
}
