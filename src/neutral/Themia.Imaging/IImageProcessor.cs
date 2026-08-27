namespace Themia.Imaging;

/// <summary>A processed image: the encoded bytes and what to name them.</summary>
/// <param name="Content">
/// The encoded bytes, positioned at the start. <b>Disposing this record disposes the stream</b>, so a
/// caller that hands it to storage should keep it alive until the write completes.
/// </param>
/// <param name="Extension">
/// File extension including the dot, e.g. <c>.webp</c>.
/// </param>
/// <param name="Width">Width of the output in pixels, after downscaling and orientation.</param>
/// <param name="Height">Height of the output in pixels, after downscaling and orientation.</param>
/// <remarks>
/// Extension rather than content-type, taken from ezy-assets' shape: storage names the object by
/// extension and derives the served content-type from it, so carrying both invites the two to disagree.
/// Dimensions are here so a caller that persists them does not have to decode the result again; there is
/// no byte count because <c>Content.Length</c> already is one.
/// </remarks>
public sealed record ProcessedImage(Stream Content, string Extension, int Width, int Height) : IDisposable
{
    /// <summary>Disposes <see cref="Content"/>.</summary>
    public void Dispose() => Content.Dispose();
}

/// <summary>
/// Normalizes an image for storage: downscale, bake in EXIF orientation, drop metadata, re-encode.
/// </summary>
/// <remarks>
/// Pure computation — no HTTP, no clock, no credentials, no I/O beyond the streams handed to it. What
/// stays with the caller: the content-type allowlist and byte limit (an HTTP concern), where the bytes
/// go (that is a storage concern), and what to do with a rejection.
/// </remarks>
public interface IImageProcessor
{
    /// <summary>
    /// Decodes <paramref name="source"/>, applies its EXIF orientation, downscales it to
    /// <see cref="ImageProcessingOptions.MaxEdge"/> (never upscaling), and re-encodes it — which is
    /// what drops the metadata.
    /// </summary>
    /// <param name="source">
    /// The image bytes. Read to the end and buffered, because a codec needs a seekable stream and an
    /// upload stream is forward-only.
    /// </param>
    /// <param name="options">
    /// Overrides for this call, or null to use the registered defaults. Per call because one consumer
    /// wants different sizes for a listing photo and an avatar.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The encoded image and its dimensions.</returns>
    /// <exception cref="ArgumentException">
    /// The bytes are not a decodable image, or the image's dimensions exceed
    /// <see cref="ImageProcessingOptions.MaxPixels"/>. Both are conditions a caller reports to whoever
    /// uploaded the file, not faults to log.
    /// </exception>
    Task<ProcessedImage> ProcessAsync(
        Stream source, ImageProcessingOptions? options = null, CancellationToken cancellationToken = default);
}
