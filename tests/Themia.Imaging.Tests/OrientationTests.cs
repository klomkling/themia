using SkiaSharp;
using Themia.Imaging.Tests.Fixtures;
using Xunit;

namespace Themia.Imaging.Tests;

/// <summary>
/// Property 2 from coord #0101: <b>orientation is baked into the pixels, not dropped.</b> Deleting the
/// metadata and honouring it are opposite operations on the same field, and shipping one without the
/// other publishes every portrait phone photo sideways.
/// </summary>
/// <remarks>
/// All eight EXIF origins, end to end through <c>ProcessAsync</c> rather than three of them at the unit
/// level — which is what the hand-built EXIF fixture buys, and what the source implementation could not
/// do because SkiaSharp's encoder writes no EXIF to round-trip through.
/// </remarks>
public sealed class OrientationTests
{
    /// <summary>Where the source's top-left marker must end up, per the EXIF specification.</summary>
    /// <remarks>
    /// Derived from the spec (1 normal, 2 mirror horizontal, 3 rotate 180, 4 mirror vertical,
    /// 5 transpose, 6 rotate 90 CW, 7 transverse, 8 rotate 90 CCW) rather than read off the
    /// implementation — a table copied from the code it checks asserts nothing.
    /// </remarks>
    public enum Quadrant { TopLeft, TopRight, BottomLeft, BottomRight }

    // EXIF tag -> the origin the codec reports, whether the transform swaps the axes, and where the
    // marker lands.
    [Theory]
    [InlineData(1, SKEncodedOrigin.TopLeft, false, Quadrant.TopLeft)]
    [InlineData(2, SKEncodedOrigin.TopRight, false, Quadrant.TopRight)]        // mirror horizontal
    [InlineData(3, SKEncodedOrigin.BottomRight, false, Quadrant.BottomRight)]  // rotate 180
    [InlineData(4, SKEncodedOrigin.BottomLeft, false, Quadrant.BottomLeft)]    // mirror vertical
    [InlineData(5, SKEncodedOrigin.LeftTop, true, Quadrant.TopLeft)]           // transpose
    [InlineData(6, SKEncodedOrigin.RightTop, true, Quadrant.TopRight)]         // rotate 90 CW
    [InlineData(7, SKEncodedOrigin.RightBottom, true, Quadrant.BottomRight)]   // transverse
    [InlineData(8, SKEncodedOrigin.LeftBottom, true, Quadrant.BottomLeft)]     // rotate 90 CCW
    public async Task Every_exif_origin_is_applied_to_the_pixels(
        int exifTag, SKEncodedOrigin origin, bool swapsAxes, Quadrant marker)
    {
        var processor = TestProcessor.Build();
        var source = TestImages.JpegWithExif(60, 40, exifTag);

        using (var codec = SKCodec.Create(new MemoryStream(source)))
        {
            // The fixture is only worth anything if the codec actually reads the tag from it.
            Assert.Equal(origin, codec!.EncodedOrigin);
        }

        using var result = await processor.ProcessAsync(new MemoryStream(source));

        var expectedWidth = swapsAxes ? 40 : 60;
        var expectedHeight = swapsAxes ? 60 : 40;
        Assert.Equal(expectedWidth, result.Width);
        Assert.Equal(expectedHeight, result.Height);

        using var decoded = SKBitmap.Decode(((MemoryStream)result.Content).ToArray());
        Assert.Equal(expectedWidth, decoded.Width);
        Assert.Equal(expectedHeight, decoded.Height);

        // Dimensions alone cannot see the four transforms that do not swap the axes: drop orientation
        // entirely and a mirror or a 180 still produces a 60x40 image. The marker is what separates
        // "honoured" from "discarded" on those.
        Assert.Equal(marker, RedQuadrant(decoded));
    }

    /// <summary>Which quadrant holds the red marker, by sampling the centre of each.</summary>
    private static Quadrant RedQuadrant(SKBitmap bitmap)
    {
        var quarterX = bitmap.Width / 4;
        var quarterY = bitmap.Height / 4;

        var samples = new (Quadrant Quadrant, SKColor Colour)[]
        {
            (Quadrant.TopLeft, bitmap.GetPixel(quarterX, quarterY)),
            (Quadrant.TopRight, bitmap.GetPixel(bitmap.Width - quarterX - 1, quarterY)),
            (Quadrant.BottomLeft, bitmap.GetPixel(quarterX, bitmap.Height - quarterY - 1)),
            (Quadrant.BottomRight, bitmap.GetPixel(bitmap.Width - quarterX - 1, bitmap.Height - quarterY - 1)),
        };

        // Lossy encoding twice over, so "reddest" rather than an exact colour — the marker is red on
        // black, and no amount of quality loss moves it to another quadrant.
        return samples.OrderByDescending(s => s.Colour.Red).First().Quadrant;
    }

    [Fact]
    public void An_upright_image_is_returned_unchanged_rather_than_copied()
    {
        using var src = Marked(4, 2);

        var result = SkiaImageProcessor.ApplyOrientation(src, SKEncodedOrigin.TopLeft);

        Assert.Same(src, result);
    }

    [Fact]
    public void A_horizontal_flip_moves_the_marker_and_keeps_the_dimensions()
    {
        using var src = Marked(4, 2);   // red at (0,0)

        using var result = SkiaImageProcessor.ApplyOrientation(src, SKEncodedOrigin.TopRight);

        Assert.Equal(4, result.Width);
        Assert.Equal(2, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(3, 0));
        Assert.NotEqual(SKColors.Red, result.GetPixel(0, 0));
    }

    [Theory]
    [InlineData(SKEncodedOrigin.BottomRight, 3, 1)]   // rotate 180: marker to the far corner
    [InlineData(SKEncodedOrigin.BottomLeft, 0, 1)]    // flip vertical: marker down the left edge
    public void A_flip_puts_the_marker_where_the_transform_says(SKEncodedOrigin origin, int x, int y)
    {
        using var src = Marked(4, 2);

        using var result = SkiaImageProcessor.ApplyOrientation(src, origin);

        Assert.Equal(SKColors.Red, result.GetPixel(x, y));
    }

    [Theory]
    [InlineData(SKEncodedOrigin.LeftTop)]
    [InlineData(SKEncodedOrigin.RightTop)]
    [InlineData(SKEncodedOrigin.RightBottom)]
    [InlineData(SKEncodedOrigin.LeftBottom)]
    public void A_ninety_degree_origin_swaps_the_axes(SKEncodedOrigin origin)
    {
        using var src = Marked(4, 2);

        using var result = SkiaImageProcessor.ApplyOrientation(src, origin);

        Assert.Equal(2, result.Width);
        Assert.Equal(4, result.Height);
    }

    private static SKBitmap Marked(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        bitmap.SetPixel(0, 0, SKColors.Red);   // a distinctive corner to track through the transform
        return bitmap;
    }
}
