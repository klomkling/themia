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
/// Register as a singleton. The only state it holds is the concurrency gate
/// (<see cref="ImageProcessingOptions.MaxConcurrency"/>), and that gate is the point: shared by every
/// caller of one registration, it is what makes the worst-case decode memory finite. A per-request
/// instance would hand each caller its own gate and bound nothing.
/// </para>
/// </remarks>
public sealed class SkiaImageProcessor : IImageProcessor
{
    // The public parameter every rejection here is about. A constant rather than nameof, because the
    // decode rejection now lives in Render, which has no parameter of that name — and reporting a
    // different paramName from the same method's other rejections would be worse than a literal.
    private const string SourceParameter = "source";

    private readonly ImageProcessingOptions defaults;

    // Not disposed, deliberately — the same call as Themia.Pdf's _renderLock. A SemaphoreSlim needs
    // disposal only when its AvailableWaitHandle was accessed (never here), and disposing one while a
    // decode sits between WaitAsync and Release turns that Release into an ObjectDisposedException.
    private readonly SemaphoreSlim slots;

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

        // Read once. A semaphore's capacity is fixed at construction, and the options object is mutable,
        // so a later mutation must not silently disagree with the real bound — which is also why the
        // per-call options ProcessAsync accepts cannot change it.
        slots = new SemaphoreSlim(defaults.MaxConcurrency, defaults.MaxConcurrency);
    }

    /// <summary>
    /// The decode gate. Exposed to this package's tests so they can occupy a slot and read
    /// <see cref="SemaphoreSlim.CurrentCount"/> directly: a timing assertion cannot tell a working gate
    /// from a slow machine, and would pass on a fast one with the gate removed.
    /// </summary>
    internal SemaphoreSlim Slots => slots;

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
            ?? throw new ArgumentException("Unsupported or corrupt image.", SourceParameter);

        var info = codec.Info;

        // BEFORE any decode. codec.Info carries the declared dimensions without materializing a pixel,
        // which is the whole point: the bomb is a file that is small on the wire and enormous decoded,
        // so a check performed after decoding is a check performed after the damage.
        if (ExceedsPixelBudget(info.Width, info.Height, effective.MaxPixels))
        {
            throw new ArgumentException(
                $"Image dimensions {info.Width}x{info.Height} exceed the {effective.MaxPixels:N0}-pixel limit.",
                SourceParameter);
        }

        // Bound concurrent decodes, and only now — after the budget refusal above, which costs nothing
        // but a header read. An oversized image must be refused without occupying a slot some other
        // caller could have used; gating first would make a bomb a denial-of-service against the queue
        // rather than merely a rejected upload.
        //
        // This is the other half of the memory budget: MaxPixels bounds one decode, and without a bound
        // on how many run at once the real ceiling is however many callers arrive together.
        // Queuing honours the token, so a caller that disconnects while queued frees its slot rather
        // than waiting for one it will not use.
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Render(codec, info, effective, cancellationToken);
        }
        finally
        {
            // finally, not a release on the success path: over-budget and undecodable both throw, and on
            // an upload endpoint a throw is ordinary user input rather than the rare case. A release that
            // only happened on success would turn the first bad upload into a permanently lost slot.
            slots.Release();
        }
    }

    /// <summary>Decodes, orients, downscales and encodes — the part that holds a slot on the decode gate.</summary>
    /// <param name="codec">The codec, already created and past the pixel-budget check.</param>
    /// <param name="info">The codec's declared image info.</param>
    /// <param name="effective">The options this call runs under.</param>
    /// <param name="cancellationToken">Checked between the synchronous stages.</param>
    /// <returns>The re-encoded image.</returns>
    private static ProcessedImage Render(
        SKCodec codec, SKImageInfo info, ImageProcessingOptions effective, CancellationToken cancellationToken)
    {
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

        // Exactly one bitmap is owned at a time. Orientation and downscale each return either their input
        // or a new bitmap, and Replace releases the input the moment a new one exists — only then, since
        // an alias must not be disposed. Holding the decoded source to the end of the call instead kept a
        // full-size buffer nobody reads alive through the encode, which is where the encoder's own working
        // memory lands on top of it (measured: a rotated 64 MP JPEG to JPEG peaked at 2.76x, not 2.0x).
        var bitmap = SKBitmap.Decode(codec, decodeInfo)
            ?? throw new ArgumentException("Could not decode image.", SourceParameter);
        try
        {
            bitmap = Replace(bitmap, ApplyOrientation(bitmap, codec.EncodedOrigin));
            bitmap = Replace(bitmap, Downscale(bitmap, effective.MaxEdge));

            cancellationToken.ThrowIfCancellationRequested();

            // Decode, orientation, downscale and encode are all synchronous, so the token is checked at
            // the boundaries between them rather than plumbed through: a 100 MP image whose client has
            // already gone away otherwise runs to completion on a pooled thread.
            //
            // Encoded from the bitmap's own pixels. SKImage.FromBitmap copies a mutable bitmap in full
            // (measured on SkiaSharp 4.151.1: exactly one extra decoded-size buffer, in every format), and
            // SKImage.Encode then encodes that copy through this same SKPixmap.Encode — so skipping it
            // changes the memory and not a byte, which OutputStabilityTests pins.
            using var pixels = bitmap.PeekPixels()
                ?? throw new InvalidOperationException("Could not read the scaled bitmap's pixels.");
            using var data = pixels.Encode(EncodedFormat(effective.Format), effective.Quality)
                ?? throw new InvalidOperationException($"{effective.Format} encoding failed.");

            var output = new MemoryStream(data.ToArray()) { Position = 0 };
            return new ProcessedImage(output, Extension(effective.Format), bitmap.Width, bitmap.Height);
        }
        finally
        {
            bitmap.Dispose();
        }
    }

    /// <summary>
    /// Hands ownership from one pipeline stage's bitmap to the next: disposes <paramref name="previous"/>
    /// when <paramref name="next"/> is a new bitmap, and leaves it alone when a stage returned it unchanged.
    /// </summary>
    /// <param name="previous">The bitmap the stage was given.</param>
    /// <param name="next">What the stage returned — possibly <paramref name="previous"/> itself.</param>
    /// <returns><paramref name="next"/>, now the only bitmap the caller owns.</returns>
    private static SKBitmap Replace(SKBitmap previous, SKBitmap next)
    {
        if (!ReferenceEquals(previous, next))
        {
            previous.Dispose();
        }

        return next;
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

        // Not canvas.DrawBitmap: in SkiaSharp that is SKImage.FromBitmap + DrawImage, and FromBitmap copies
        // a mutable bitmap in full — a third decoded-size buffer alive alongside `src` and `dst` for the
        // whole draw (measured: 3.01x the decoded size on a 64 MP JPEG, against 2.0x this way).
        // FromPixels wraps `src`'s memory without copying; the wrapper is disposed before this returns,
        // so it cannot outlive the bitmap it points into.
        using var pixels = src.PeekPixels();
        using var image = SKImage.FromPixels(pixels);
        canvas.SetMatrix(OrientationMatrix(origin, src.Width, src.Height));
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return dst;
    }

    /// <summary>A downscaled copy when the longest edge exceeds <paramref name="maxEdge"/>; otherwise the input unchanged.</summary>
    internal static SKBitmap Downscale(SKBitmap src, int maxEdge)
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
    internal static SKMatrix OrientationMatrix(SKEncodedOrigin origin, int w, int h) => origin switch
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
