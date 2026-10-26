using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Themia.Payments.TwoCTwoP.Internal;

/// <summary>
/// 2C2P's key-based envelope for the Payment Maintenance API: the XML is encrypted to 2C2P's public key as a JWE
/// (<c>RSA-OAEP</c> + <c>A256GCM</c>) and the JWE is signed with the merchant's private key as a JWS (<c>PS256</c>).
/// A response is verified with 2C2P's public key BEFORE it is decrypted with the merchant's private key. The three
/// algorithms are pinned: a header that names anything else, or carries a key-selecting member, is refused.
/// </summary>
internal static class JweJws
{
    private const string JwsHeader = """{"alg":"PS256","typ":"JWT"}""";
    private const string JweHeader = """{"alg":"RSA-OAEP","enc":"A256GCM"}""";
    private const int ContentKeyBytes = 32;
    private const int IvBytes = 12;
    private const int TagBytes = 16;

    public static string Seal(string xml, RSA providerPublicKey, RSA merchantPrivateKey)
    {
        var protectedHeader = Base64Url.Encode(Encoding.ASCII.GetBytes(JweHeader));
        var contentKey = RandomNumberGenerator.GetBytes(ContentKeyBytes);
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var plaintext = Encoding.UTF8.GetBytes(xml);
        var cipherText = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(contentKey, TagBytes))
        {
            aes.Encrypt(iv, plaintext, cipherText, tag, Encoding.ASCII.GetBytes(protectedHeader));
        }

        var encryptedKey = providerPublicKey.Encrypt(contentKey, RSAEncryptionPadding.OaepSHA1);
        var jwe = string.Join('.', protectedHeader, Base64Url.Encode(encryptedKey), Base64Url.Encode(iv), Base64Url.Encode(cipherText), Base64Url.Encode(tag));

        var signingInput = Base64Url.Encode(Encoding.ASCII.GetBytes(JwsHeader)) + "." + Base64Url.Encode(Encoding.ASCII.GetBytes(jwe));
        var signature = merchantPrivateKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return signingInput + "." + Base64Url.Encode(signature);
    }

    /// <summary>Verifies and decrypts a response; <see langword="false"/> for anything that does not check out, with no detail on why.</summary>
    public static bool TryOpen(string jws, RSA providerPublicKey, RSA merchantPrivateKey, out string xml)
    {
        xml = "";
        try
        {
            var parts = jws.Trim().Split('.');
            if (parts.Length != 3 || !HeaderIs(parts[0], "PS256", enc: null))
            {
                return false;
            }

            var signature = Base64Url.Decode(parts[2]);
            if (!providerPublicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            {
                return false;
            }

            var jwe = Encoding.ASCII.GetString(Base64Url.Decode(parts[1])).Split('.');
            if (jwe.Length != 5 || !HeaderIs(jwe[0], "RSA-OAEP", enc: "A256GCM"))
            {
                return false;
            }

            var contentKey = merchantPrivateKey.Decrypt(Base64Url.Decode(jwe[1]), RSAEncryptionPadding.OaepSHA1);
            var iv = Base64Url.Decode(jwe[2]);
            var cipherText = Base64Url.Decode(jwe[3]);
            var tag = Base64Url.Decode(jwe[4]);
            if (contentKey.Length != ContentKeyBytes || iv.Length != IvBytes || tag.Length != TagBytes)
            {
                return false;
            }

            var plaintext = new byte[cipherText.Length];
            using (var aes = new AesGcm(contentKey, TagBytes))
            {
                // The received header segment is the AAD, never a re-serialised one.
                aes.Decrypt(iv, cipherText, tag, plaintext, Encoding.ASCII.GetBytes(jwe[0]));
            }

            xml = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(plaintext);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool HeaderIs(string segment, string alg, string? enc)
    {
        using var doc = JsonDocument.Parse(Base64Url.Decode(segment));
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var members = 0;
        foreach (var member in doc.RootElement.EnumerateObject())
        {
            members++;
            var allowed = member.Name switch
            {
                "alg" => member.Value.ValueKind == JsonValueKind.String && member.Value.GetString() == alg,
                "enc" => enc is not null && member.Value.ValueKind == JsonValueKind.String && member.Value.GetString() == enc,
                "typ" => member.Value.ValueKind == JsonValueKind.String && member.Value.GetString() == "JWT",
                _ => false, // zip, crit, jwk, jku, x5u, kid, ... : nothing may choose a key or alter the content
            };
            if (!allowed)
            {
                return false;
            }
        }

        return doc.RootElement.TryGetProperty("alg", out _) && (enc is null || doc.RootElement.TryGetProperty("enc", out _)) && members > 0;
    }
}

internal static class Base64Url
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        return Convert.FromBase64String(s);
    }
}
