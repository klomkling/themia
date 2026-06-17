using System.Security.Cryptography;
using System.Text;

namespace Themia.Storage.Local;

/// <summary>Signs and verifies Local presigned-URL tokens with HMAC-SHA256 over
/// <c>key|operation|expiryUnixSeconds</c>. The module's download/upload endpoint verifies the token
/// before serving, giving the Local backend the same time-limited, tamper-evident URLs as S3/R2.</summary>
public sealed class LocalUrlSigner
{
    private readonly byte[] keyBytes;

    /// <summary>Creates the signer.</summary>
    /// <param name="signingKey">The shared HMAC key (keep secret; never log it).</param>
    public LocalUrlSigner(string signingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        keyBytes = Encoding.UTF8.GetBytes(signingKey);
    }

    /// <summary>Produces a token authorizing <paramref name="operation"/> on <paramref name="key"/> until
    /// <paramref name="expiresAt"/>, formatted as <c>{expiryUnix}.{base64urlSignature}</c>.</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="operation">The operation tag (e.g. "get"/"put").</param>
    /// <param name="expiresAt">When the token expires.</param>
    /// <returns>The signed token.</returns>
    public string Sign(string key, string operation, DateTimeOffset expiresAt)
    {
        var expiry = expiresAt.ToUnixTimeSeconds();
        var signature = Compute(key, operation, expiry);
        return $"{expiry}.{signature}";
    }

    /// <summary>Verifies <paramref name="token"/> for <paramref name="key"/>/<paramref name="operation"/>
    /// at <paramref name="now"/> (constant-time compare; rejects malformed or expired tokens).</summary>
    /// <param name="key">The physical object key.</param>
    /// <param name="operation">The operation tag.</param>
    /// <param name="token">The token to verify.</param>
    /// <param name="now">The current time.</param>
    /// <returns><see langword="true"/> when valid and unexpired.</returns>
    public bool TryVerify(string key, string operation, string token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0 || !long.TryParse(token.AsSpan(0, dot), out var expiry)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(expiry) < now) return false;

        var expected = Compute(key, operation, expiry);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token[(dot + 1)..]), Encoding.UTF8.GetBytes(expected));
    }

    private string Compute(string key, string operation, long expiry)
    {
        var payload = Encoding.UTF8.GetBytes($"{key}|{operation}|{expiry}");
        var hash = HMACSHA256.HashData(keyBytes, payload);
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
