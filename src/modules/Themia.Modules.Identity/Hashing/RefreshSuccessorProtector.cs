using System.Security.Cryptography;
using System.Text;

namespace Themia.Modules.Identity.Hashing;

/// <summary>
/// Seals a refresh token's successor so that only a holder of the rotated token's own raw value can read it back (the refresh reuse
/// grace). The key is HKDF-SHA256 of the rotated token's raw value, the cipher is AES-GCM, and the row's id is bound as associated
/// data. The raw value is never stored, so a database leak alone yields nothing; there is no server secret to provision or rotate.
/// </summary>
internal static class RefreshSuccessorProtector
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly byte[] Info = "themia-refresh-successor-v1"u8.ToArray();

    /// <summary>Seals <paramref name="successorRaw"/> under <paramref name="parentRaw"/>.</summary>
    public static string Protect(string parentRaw, Guid parentId, string successorRaw)
    {
        var plaintext = Encoding.UTF8.GetBytes(successorRaw);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(DeriveKey(parentRaw), TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, parentId.ToByteArray());
        }

        var sealedValue = new byte[NonceBytes + ciphertext.Length + TagBytes];
        nonce.CopyTo(sealedValue, 0);
        ciphertext.CopyTo(sealedValue, NonceBytes);
        tag.CopyTo(sealedValue, NonceBytes + ciphertext.Length);
        return Convert.ToBase64String(sealedValue).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Opens a value sealed by <see cref="Protect"/>; null when it is malformed or was not sealed under this raw value and row.</summary>
    public static string? Unprotect(string parentRaw, Guid parentId, string sealedText)
    {
        try
        {
            var padded = sealedText.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var sealedValue = Convert.FromBase64String(padded);
            if (sealedValue.Length <= NonceBytes + TagBytes)
            {
                return null;
            }

            var nonce = sealedValue.AsSpan(0, NonceBytes);
            var ciphertext = sealedValue.AsSpan(NonceBytes, sealedValue.Length - NonceBytes - TagBytes);
            var tag = sealedValue.AsSpan(sealedValue.Length - TagBytes, TagBytes);
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(DeriveKey(parentRaw), TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, parentId.ToByteArray());
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static byte[] DeriveKey(string parentRaw) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(parentRaw), KeyBytes, info: Info);
}
