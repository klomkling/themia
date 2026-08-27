namespace Themia.Imaging;

/// <summary>The encoded format <see cref="IImageProcessor"/> writes.</summary>
public enum ImageOutputFormat
{
    /// <summary>WebP. The default — visually indistinguishable from JPEG at a fraction of the bytes.</summary>
    Webp,

    /// <summary>JPEG. For a consumer whose delivery path cannot serve WebP.</summary>
    Jpeg,

    /// <summary>PNG. Lossless, and much larger for photographs; <see cref="ImageProcessingOptions.Quality"/> is ignored.</summary>
    Png,
}

/// <summary>How <see cref="IImageProcessor"/> normalizes an image.</summary>
/// <remarks>
/// Defaults are the constants ezy-assets has run in production (coord #0101): 1600px, quality 80,
/// WebP, 100 megapixels.
/// </remarks>
public sealed class ImageProcessingOptions
{
    /// <summary>Longest edge of the output, in pixels. An image already within it is left alone — this never upscales.</summary>
    public int MaxEdge { get; set; } = 1600;

    /// <summary>Encoder quality, 1–100. Ignored for <see cref="ImageOutputFormat.Png"/>.</summary>
    public int Quality { get; set; } = 80;

    /// <summary>The encoded output format.</summary>
    public ImageOutputFormat Format { get; set; } = ImageOutputFormat.Webp;

    /// <summary>
    /// The decompression-bomb budget: the most decoded pixels this will accept, checked from the
    /// codec's header <b>before</b> anything is decoded.
    /// </summary>
    /// <remarks>
    /// An upload byte-limit bounds the <i>encoded</i> size and says nothing about the decoded one. A
    /// solid-colour 12000×12000 PNG is <b>48 bytes</b> on the wire — the fixture in this package's own
    /// tests is exactly that — and decodes to 144 MB; at 30000×30000 it is still tiny on the wire and
    /// decodes to 900 MB. An endpoint with a 10 MB limit looks fully guarded and OOMs the box.
    /// <para>
    /// 100 MP admits every mainstream phone camera (12–64 MP) while refusing the pathological case.
    /// </para>
    /// </remarks>
    public long MaxPixels { get; set; } = 100_000_000;

    /// <summary>Reports the first configuration problem, or null when the options are usable.</summary>
    /// <remarks>
    /// Shared by the per-call check and the <c>ValidateOnStart</c> registration, so a bad value is
    /// refused at boot rather than on somebody's first upload.
    /// </remarks>
    internal string? Validate()
    {
        if (MaxEdge < 1)
        {
            return $"MaxEdge must be at least 1, but was {MaxEdge}.";
        }

        if (Quality is < 1 or > 100)
        {
            return $"Quality must be between 1 and 100, but was {Quality}.";
        }

        if (MaxPixels < 1)
        {
            return $"MaxPixels must be at least 1, but was {MaxPixels}.";
        }

        if (!Enum.IsDefined(typeof(ImageOutputFormat), Format))
        {
            return $"Format {(int)Format} is not a defined ImageOutputFormat.";
        }

        return null;
    }
}
