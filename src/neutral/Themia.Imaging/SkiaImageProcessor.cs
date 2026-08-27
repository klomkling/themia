using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Themia.Imaging;

/// <inheritdoc cref="IImageProcessor" />
/// <remarks>
/// Ported from ezy-assets' production <c>SkiaSharpImageProcessor</c> (coord #0101) rather than
/// redesigned: the pre-decode budget read from the codec header, the power-of-two subsample (JPEG and
/// WebP only — Skia cannot subsample a PNG), the orientation matrix for all eight EXIF origins, and the
/// disposal that checks <see cref="object.ReferenceEquals"/> before disposing an alias.
/// <para>
/// Stateless — register as a singleton.
/// </para>
/// </remarks>
public sealed class SkiaImageProcessor : IImageProcessor
{
    private readonly ImageProcessingOptions defaults;

    /// <summary>Creates the processor.</summary>
    /// <param name="options">The default processing options, used when a call passes none.</param>
    public SkiaImageProcessor(IOptions<ImageProcessingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        defaults = options.Value ?? throw new ArgumentNullException(nameof(options));

        // Also validated at startup by AddThemiaImaging, so a bad value fails the boot rather than the
        // first upload. Kept here too: this type is constructible directly, including by a test.
        if (defaults.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }
    }

    /// <inheritdoc />
    public async Task<ProcessedImage> ProcessAsync(
        Stream source, ImageProcessingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var effective = options ?? defaults;
        if (!ReferenceEquals(effective, defaults) && effective.Validate() is { } problem)
        {
            throw new ArgumentException(problem, nameof(options));
        }

        // A codec needs a seekable stream; a multipart upload stream is forward-only. Buffer first.
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;

        using var codec = SKCodec.Create(buffer)
            ?? throw new ArgumentException("Unsupported or corrupt image.", nameof(source));

        var info = codec.Info;

        // BEFORE any decode. codec.Info carries the declared dimensions without materializing a pixel,
        // which is the whole point: the bomb is a file that is small on the wire and enormous decoded,
        // so a check performed after decoding is a check performed after the damage.
        if (ExceedsPixelBudget(info.Width, info.Height, effective.MaxPixels))
        {
            throw new ArgumentException(
                $"Image dimensions {info.Width}x{info.Height} exceed the {effective.MaxPixels:N0}-pixel limit.",
                nameof(source));
        }

        // Decode at the largest power-of-two subsample whose long edge still clears MaxEdge, so a very
        // large image never materializes at full resolution; Downscale then trims precisely. Quality is
        // unaffected — the decode is always at least the target size before the final resize.
        var scale = SubsampleScale(Math.Max(info.Width, info.Height), effective.MaxEdge);
        var decodeDims = codec.GetScaledDimensions(scale);

        // sRGB as the destination colour space, explicitly. Omitting it does not mean "keep the
        // source's" — it means the codec performs no colour transform and the encoder writes no ICC
        // profile, so a wide-gamut source lands as untagged bytes every viewer then reads as sRGB.
        // Measured on a Display P3 red: without this the output pixel is #ea3323, with it #ff0000.
        // iPhones shoot Display P3 by default, so that is the common case, not the exotic one — and an
        // untagged file carries nothing to recover the intent from.
        var decodeInfo = new SKImageInfo(
            decodeDims.Width, decodeDims.Height, info.ColorType, info.AlphaType, SKColorSpace.CreateSrgb());

        cancellationToken.ThrowIfCancellationRequested();
        using var decoded = SKBitmap.Decode(codec, decodeInfo)
            ?? throw new ArgumentException("Could not decode image.", nameof(source));

        // `decoded` is owned by its `using`. `oriented`/`scaled` are allocated inside the try so a throw
        // from either still disposes whatever was allocated — and only when they are new bitmaps rather
        // than aliases of `decoded` or of each other.
        SKBitmap? oriented = null;
        SKBitmap? scaled = null;
        try
        {
            oriented = ApplyOrientation(decoded, codec.EncodedOrigin);
            scaled = Downscale(oriented, effective.MaxEdge);

            cancellationToken.ThrowIfCancellationRequested();

            // Decode, orientation, downscale and encode are all synchronous, so the token is checked at
            // the boundaries between them rather than plumbed through: a 100 MP image whose client has
            // already gone away otherwise runs to completion on a pooled thread.
            using var image = SKImage.FromBitmap(scaled)
                ?? throw new InvalidOperationException("Could not read the scaled bitmap's pixels.");
            using var data = image.Encode(EncodedFormat(effective.Format), effective.Quality)
                ?? throw new InvalidOperationException($"{effective.Format} encoding failed.");

            var output = new MemoryStream(data.ToArray()) { Position = 0 };
            return new ProcessedImage(output, Extension(effective.Format), scaled.Width, scaled.Height);
        }
        finally
        {
            if (scaled is not null && !ReferenceEquals(scaled, oriented))
            {
                scaled.Dispose();
            }

            if (oriented is not null && !ReferenceEquals(oriented, decoded))
            {
                oriented.Dispose();
            }
        }
    }

    /// <summary>True when width × height exceeds <paramref name="maxPixels"/> — the decompression-bomb guard.</summary>
    /// <param name="width">Declared width.</param>
    /// <param name="height">Declared height.</param>
    /// <param name="maxPixels">The budget.</param>
    /// <returns>Whether the image is over budget.</returns>
    /// <remarks><c>long</c> multiplication on purpose: 60000 × 60000 overflows <see cref="int"/> to a negative.</remarks>
    internal static bool ExceedsPixelBudget(int width, int height, long maxPixels) =>
        (long)width * height > maxPixels;

    /// <summary>
    /// The decode scale (<c>1/2^n</c>) for the largest power-of-two subsample whose long edge is still
    /// at least <paramref name="maxEdge"/>, so a large image is never materialized at full resolution.
    /// Returns 1 for an image already within <paramref name="maxEdge"/>.
    /// </summary>
    /// <param name="longestEdge">The image's longest edge.</param>
    /// <param name="maxEdge">The target longest edge.</param>
    /// <returns>The decode scale.</returns>
    internal static float SubsampleScale(int longestEdge, int maxEdge)
    {
        var factor = 1;
        while (longestEdge / (factor * 2) >= maxEdge)
        {
            factor *= 2;
        }

        return 1f / factor;
    }

    /// <summary>
    /// Applies an EXIF orientation to the pixels, so the stored image is upright and the tag is no
    /// longer needed. Returns the input unchanged for an already-upright origin.
    /// </summary>
    /// <param name="src">The decoded bitmap.</param>
    /// <param name="origin">The origin the codec read from the file.</param>
    /// <returns>An upright bitmap, which may be <paramref name="src"/> itself.</returns>
    /// <remarks>
    /// Dropping the metadata and honouring it are opposite operations on the same field, and shipping
    /// one without the other publishes every portrait phone photo sideways.
    /// <para>
    /// Internal, not public: it returns <paramref name="src"/> itself for an already-upright origin, so
    /// a caller writing the obvious <c>using var upright = ApplyOrientation(src, origin);</c> would
    /// dispose its own bitmap and carry on using it. Nobody asked for it as surface.
    /// </para>
    /// </remarks>
    internal static SKBitmap ApplyOrientation(SKBitmap src, SKEncodedOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(src);

        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return src;
        }

        var rotated = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = rotated ? src.Height : src.Width;
        var height = rotated ? src.Width : src.Height;

        var dst = new SKBitmap(width, height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.SetMatrix(OrientationMatrix(origin, src.Width, src.Height));
        canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return dst;
    }

    /// <summary>A downscaled copy when the longest edge exceeds <paramref name="maxEdge"/>; otherwise the input unchanged.</summary>
    private static SKBitmap Downscale(SKBitmap src, int maxEdge)
    {
        var longest = Math.Max(src.Width, src.Height);
        if (longest <= maxEdge)
        {
            return src;
        }

        var scale = (float)maxEdge / longest;
        var width = Math.Max(1, (int)Math.Round(src.Width * scale));
        var height = Math.Max(1, (int)Math.Round(src.Height * scale));

        var dst = new SKBitmap(width, height, src.ColorType, src.AlphaType);

        // ScalePixels reports failure by returning false, and the usual cause is that `dst` could not
        // allocate its pixels. Ignoring it hands the caller a blank image that encodes perfectly well.
        if (!src.ScalePixels(dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)))
        {
            dst.Dispose();
            throw new InvalidOperationException(
                $"Could not scale the image to {width}x{height}; the destination pixels were not available.");
        }

        return dst;
    }

    /// <summary>The affine transform mapping a source pixel to its upright position, per EXIF origin.</summary>
    private static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => SKMatrix.CreateScaleTranslation(-1, 1, w, 0),      // flip horizontal
        SKEncodedOrigin.BottomRight => SKMatrix.CreateScaleTranslation(-1, -1, w, h),  // rotate 180
        SKEncodedOrigin.BottomLeft => SKMatrix.CreateScaleTranslation(1, -1, 0, h),    // flip vertical
        SKEncodedOrigin.LeftTop => new SKMatrix { ScaleX = 0, SkewX = 1, TransX = 0, SkewY = 1, ScaleY = 0, TransY = 0, Persp2 = 1 },       // transpose
        SKEncodedOrigin.RightTop => new SKMatrix { ScaleX = 0, SkewX = -1, TransX = h, SkewY = 1, ScaleY = 0, TransY = 0, Persp2 = 1 },     // rotate 90 CW
        SKEncodedOrigin.RightBottom => new SKMatrix { ScaleX = 0, SkewX = -1, TransX = h, SkewY = -1, ScaleY = 0, TransY = w, Persp2 = 1 }, // transverse
        SKEncodedOrigin.LeftBottom => new SKMatrix { ScaleX = 0, SkewX = 1, TransX = 0, SkewY = -1, ScaleY = 0, TransY = w, Persp2 = 1 },   // rotate 90 CCW
        _ => SKMatrix.Identity,
    };

    private static SKEncodedImageFormat EncodedFormat(ImageOutputFormat format) => format switch
    {
        ImageOutputFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        ImageOutputFormat.Png => SKEncodedImageFormat.Png,
        _ => SKEncodedImageFormat.Webp,
    };

    private static string Extension(ImageOutputFormat format) => format switch
    {
        ImageOutputFormat.Jpeg => ".jpg",
        ImageOutputFormat.Png => ".png",
        _ => ".webp",
    };
}
