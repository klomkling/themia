using System.Security.Cryptography;
using System.Text;

namespace Themia.Conversations;

/// <summary>
/// The secret in a guest's session cookie. It exposes no member that returns the text, so a logger that destructures an actor (<c>{@Actor}</c>) or a JSON
/// serialiser cannot write it out; <see cref="ToString"/> is redacted. The app builds it from the cookie value; the store only ever compares its hash.
/// </summary>
public sealed class GuestSessionKey : IEquatable<GuestSessionKey>
{
    private const int KeyBytes = 32;
    private const int EncodedLength = 43; // 32 bytes, base64url, no padding

    /// <summary>Wraps a cookie value.</summary>
    /// <param name="value">The 43-character base64url form the exchange issued.</param>
    /// <exception cref="ArgumentException">The value is not in that form.</exception>
    public GuestSessionKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsWellFormed(value))
        {
            throw new ArgumentException("A guest session key is the 43-character base64url value the exchange issued.", nameof(value));
        }

        Value = value;
    }

    internal string Value { get; }

    /// <summary>Whether <paramref name="value"/> is a well-formed key; use it to refuse a malformed cookie without throwing.</summary>
    /// <param name="value">The cookie value.</param>
    /// <param name="key">The key, or <see langword="null"/> when the value is malformed.</param>
    public static bool TryParse(string? value, out GuestSessionKey? key)
    {
        key = value is not null && IsWellFormed(value) ? new GuestSessionKey(value) : null;
        return key is not null;
    }

    /// <inheritdoc />
    public override string ToString() => "[redacted]";

    /// <inheritdoc />
    public bool Equals(GuestSessionKey? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GuestSessionKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    internal static GuestSessionKey Generate() => new(Encode(RandomNumberGenerator.GetBytes(KeyBytes)));

    /// <summary>The value stored in place of the key: its SHA-256 (the key has 256 bits of entropy, so no salt is needed).</summary>
    internal byte[] Hash() => SHA256.HashData(Encoding.ASCII.GetBytes(Value));

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsWellFormed(string value)
    {
        if (value.Length != EncodedLength)
        {
            return false;
        }

        try
        {
            // Canonical only: the one encoding of the 32 bytes, so two spellings of a key cannot both hash.
            var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == KeyBytes && string.Equals(Encode(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
