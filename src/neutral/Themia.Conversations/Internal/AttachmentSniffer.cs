namespace Themia.Conversations.Internal;

/// <summary>Checks that the bytes of an upload look like the type they declare. The declared type is never trusted on its own.</summary>
internal static class AttachmentSniffer
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Pdf = "%PDF-"u8.ToArray();
    private static readonly byte[] Riff = "RIFF"u8.ToArray();
    private static readonly byte[] Webp = "WEBP"u8.ToArray();

    public static bool IsImage(string contentType) =>
        contentType is "image/jpeg" or "image/png" or "image/webp";

    public static bool Matches(string contentType, byte[] bytes) => contentType switch
    {
        "image/jpeg" => bytes.AsSpan().StartsWith(Jpeg),
        "image/png" => bytes.AsSpan().StartsWith(Png),
        "image/webp" => bytes.Length >= 12 && bytes.AsSpan().StartsWith(Riff) && bytes.AsSpan(8, 4).SequenceEqual(Webp),
        "application/pdf" => bytes.AsSpan().StartsWith(Pdf),
        _ => false, // a type on the allow-list with no signature here is refused: add its signature before allowing it
    };

    /// <summary>The content type of an encoded image, from the extension <c>IImageProcessor</c> reports.</summary>
    public static string ImageTypeOf(string extension) => extension.TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "image/jpeg",
        "png" => "image/png",
        "webp" => "image/webp",
        _ => throw new InvalidOperationException($"The image processor produced an unsupported format '{extension}'."),
    };
}
