namespace Themia.Totp;

/// <summary>
/// RFC 4648 base32, the encoding authenticator applications expect for a shared secret.
/// </summary>
/// <remarks>Internal: the BCL has no base32, and this is not surface worth exposing.</remarks>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    internal static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var output = new System.Text.StringBuilder((data.Length + 4) / 5 * 8);
        for (var offset = 0; offset < data.Length; offset += 5)
        {
            var chunk = data.Slice(offset, Math.Min(5, data.Length - offset));

            ulong buffer = 0;
            for (var i = 0; i < 5; i++)
            {
                buffer = (buffer << 8) | (i < chunk.Length ? chunk[i] : 0u);
            }

            // 5 bytes -> 8 base32 characters; pad the characters the missing bytes would have produced.
            var significant = (chunk.Length * 8 + 4) / 5;
            for (var i = 0; i < 8; i++)
            {
                output.Append(i < significant ? Alphabet[(int)((buffer >> (35 - (i * 5))) & 0x1F)] : '=');
            }
        }

        return output.ToString();
    }

    /// <summary>Decodes base32, ignoring padding, whitespace and case as authenticator apps do.</summary>
    /// <exception cref="FormatException">A character is not in the base32 alphabet.</exception>
    internal static byte[] Decode(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        var bits = 0;
        var accumulator = 0;
        var output = new List<byte>(encoded.Length * 5 / 8);

        foreach (var raw in encoded)
        {
            if (raw is '=' or ' ' or '-' or '\t' or '\r' or '\n')
            {
                continue;
            }

            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0)
            {
                throw new FormatException($"'{raw}' is not a base32 character.");
            }

            accumulator = (accumulator << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((accumulator >> bits) & 0xFF));
            }
        }

        return [.. output];
    }
}
