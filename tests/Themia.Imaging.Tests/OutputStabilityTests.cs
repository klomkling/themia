using SkiaSharp;
using Themia.Imaging.Tests.Fixtures;
using Xunit;

namespace Themia.Imaging.Tests;

/// <summary>
/// The memory ceiling was lowered by changing <i>how</i> pixels reach the orientation draw and the
/// encoder — never <i>which</i> pixels, sampling or encoder settings. So every processed image must come
/// out byte-for-byte what the previous pipeline produced.
/// </summary>
/// <remarks>
/// <see cref="PreviousPipeline"/> is that pipeline frozen as it shipped: the orientation drawn through
/// <see cref="SKCanvas.DrawBitmap(SKBitmap, float, float, SKSamplingOptions, SKPaint)"/> and the result
/// encoded through <see cref="SKImage.FromBitmap"/>, each of which copies a mutable bitmap in full. A copy
/// cannot change a pixel, and this is what makes that a fact rather than an expectation.
/// <para>
/// Exact equality on purpose. "Similar size" would pass a silent change to every image this package has
/// ever processed, which is a worse outcome than the memory the change saves.
/// </para>
/// </remarks>
public sealed class OutputStabilityTests
{
    private const int Width = 360;
    private const int Height = 240;

    public static TheoryData<string, int, ImageOutputFormat> Cases()
    {
        var sources = new List<string>();
        sources.AddRange(Enumerable.Range(1, 8).Select(o => $"jpeg-exif{o}"));
        sources.AddRange(["png", "webp", "p3-jpeg-exif6"]);

        var data = new TheoryData<string, int, ImageOutputFormat>();
        foreach (var source in sources)
        {
            // 1000: no downscale. 300: a downscale from a full decode. 150: a JPEG/WebP subsample (360/2
            // still clears 150) followed by the downscale — each stage the memory change touches.
            foreach (var maxEdge in new[] { 1000, 300, 150 })
            {
                foreach (var format in Enum.GetValues<ImageOutputFormat>())
                {
                    data.Add(source, maxEdge, format);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task The_output_is_byte_identical_to_the_previous_pipeline(
        string source, int maxEdge, ImageOutputFormat format)
    {
        var input = Source(source);
        var options = new ImageProcessingOptions { MaxEdge = maxEdge, Format = format };
        var expected = PreviousPipeline(input, options);

        using var processed = await TestProcessor.Build().ProcessAsync(new MemoryStream(input), options);
        using var actual = new MemoryStream();
        await processed.Content.CopyToAsync(actual);

        Assert.Equal(expected, actual.ToArray());
    }

    private static byte[] Source(string name) => name switch
    {
        "png" => TestImages.Detailed(Width, Height, SKEncodedImageFormat.Png),
        "webp" => TestImages.Detailed(Width, Height, SKEncodedImageFormat.Webp),
        "p3-jpeg-exif6" => TestImages.WithExif(TestImages.WideGamutRedJpeg(Width, Height), 6),
        _ => TestImages.WithExif(
            TestImages.Detailed(Width, Height, SKEncodedImageFormat.Jpeg), int.Parse(name["jpeg-exif".Length..])),
    };

    /// <summary>The pipeline before the memory change, frozen. Only the two copying calls differ from today's.</summary>
    private static byte[] PreviousPipeline(byte[] input, ImageProcessingOptions options)
    {
        using var codec = SKCodec.Create(new MemoryStream(input))!;
        var info = codec.Info;
        var dims = codec.GetScaledDimensions(
            SkiaImageProcessor.SubsampleScale(Math.Max(info.Width, info.Height), options.MaxEdge));
        using var decoded = SKBitmap.Decode(
            codec, new SKImageInfo(dims.Width, dims.Height, info.ColorType, info.AlphaType, SKColorSpace.CreateSrgb()))!;

        var oriented = OrientThroughDrawBitmap(decoded, codec.EncodedOrigin);
        var scaled = SkiaImageProcessor.Downscale(oriented, options.MaxEdge);
        try
        {
            using var image = SKImage.FromBitmap(scaled);
            using var data = image.Encode(Encoded(options.Format), options.Quality);
            return data.ToArray();
        }
        finally
        {
            if (!ReferenceEquals(scaled, oriented))
            {
                scaled.Dispose();
            }

            if (!ReferenceEquals(oriented, decoded))
            {
                oriented.Dispose();
            }
        }
    }

    private static SKBitmap OrientThroughDrawBitmap(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return src;
        }

        var rotated = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(rotated ? src.Height : src.Width, rotated ? src.Width : src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.SetMatrix(SkiaImageProcessor.OrientationMatrix(origin, src.Width, src.Height));
        canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        return dst;
    }

    private static SKEncodedImageFormat Encoded(ImageOutputFormat format) => format switch
    {
        ImageOutputFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        ImageOutputFormat.Png => SKEncodedImageFormat.Png,
        _ => SKEncodedImageFormat.Webp,
    };
}
