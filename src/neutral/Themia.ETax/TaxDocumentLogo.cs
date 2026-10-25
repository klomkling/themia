namespace Themia.ETax;

/// <summary>
/// The seller's logo, printed in the logo slot of the A5 templates. A PNG or JPEG of at most <see cref="MaxBytes"/>: it is embedded in the page, so
/// anything bigger bloats every PDF. Passed when rendering; it is not part of the stored document, so a document re-rendered without it prints no logo box.
/// </summary>
public sealed class TaxDocumentLogo
{
    /// <summary>The largest image accepted, in bytes.</summary>
    public const int MaxBytes = 256 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private readonly byte[] _bytes;

    /// <summary>Wraps the image, checking its size and that it really is a PNG or a JPEG (by its first bytes, not its name).</summary>
    /// <param name="bytes">The image file.</param>
    /// <exception cref="ArgumentException">The image is empty, larger than <see cref="MaxBytes"/>, or neither a PNG nor a JPEG.</exception>
    public TaxDocumentLogo(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
            throw new ArgumentException("The logo image is empty.", nameof(bytes));
        if (bytes.Length > MaxBytes)
            throw new ArgumentException($"The logo image is {bytes.Length} bytes; the limit is {MaxBytes}.", nameof(bytes));

        MediaType = bytes.AsSpan().StartsWith(PngSignature) ? "image/png"
            : bytes.AsSpan().StartsWith(JpegSignature) ? "image/jpeg"
            : throw new ArgumentException("The logo image must be a PNG or a JPEG.", nameof(bytes));
        _bytes = (byte[])bytes.Clone();
    }

    /// <summary><c>image/png</c> or <c>image/jpeg</c>.</summary>
    public string MediaType { get; }

    internal string ToDataUri() => $"data:{MediaType};base64,{Convert.ToBase64String(_bytes)}";
}
