namespace Themia.ETax.Withholding;

/// <summary>
/// The payer's signature as an image the adopter supplies when rendering, printed on the "ลงชื่อ … ผู้จ่ายเงิน" line. Like the copy
/// markings it is a render-time input: it is never part of the snapshot or its hash, so the same stored certificate prints with or without
/// it, and the adopter decides who signs. PNG or JPEG, at most <see cref="MaxBytes"/>.
/// </summary>
public sealed class WithholdingSignatureImage
{
    /// <summary>The largest image accepted: 256 KiB. A signature is a small picture; a larger file only slows every render.</summary>
    public const int MaxBytes = 256 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private readonly byte[] _bytes;

    /// <summary>Wraps the image bytes after checking them.</summary>
    /// <param name="bytes">The PNG or JPEG file.</param>
    /// <exception cref="ArgumentException">The bytes are empty, larger than <see cref="MaxBytes"/>, or neither a PNG nor a JPEG.</exception>
    public WithholdingSignatureImage(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw new ArgumentException("The signature image is empty.", nameof(bytes));
        if (bytes.Length > MaxBytes)
            throw new ArgumentException($"The signature image is {bytes.Length} bytes; the limit is {MaxBytes}.", nameof(bytes));

        MediaType = bytes.AsSpan().StartsWith(PngSignature) ? "image/png"
            : bytes.AsSpan().StartsWith(JpegSignature) ? "image/jpeg"
            : throw new ArgumentException("The signature image must be a PNG or a JPEG.", nameof(bytes));
        _bytes = (byte[])bytes.Clone();
    }

    /// <summary><c>image/png</c> or <c>image/jpeg</c>.</summary>
    public string MediaType { get; }

    /// <summary>The image size in bytes.</summary>
    public int Length => _bytes.Length;

    /// <summary>The lowercase hex SHA-256 of the image bytes: what is recorded when a certificate is issued with this signature.</summary>
    public string Sha256 => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(_bytes)).ToLowerInvariant();

    internal string ToDataUri() => $"data:{MediaType};base64,{Convert.ToBase64String(_bytes)}";
}
