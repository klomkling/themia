using SkiaSharp;
using Themia.Imaging.Tests.Fixtures;
using Xunit;

namespace Themia.Imaging.Tests;

/// <summary>
/// A wide-gamut source must not be silently desaturated. Not one of coord #0101's four properties —
/// found by review of this port — and it belongs with them: the output is a valid image, of visibly
/// the wrong colour, carrying no profile to recover the intent from.
/// </summary>
public sealed class ColourTests
{
    [Fact]
    public void The_fixture_really_is_wide_gamut()
    {
        // Without this, the test below could pass against a plain sRGB source and prove nothing.
        var source = TestImages.WideGamutRedJpeg(40, 20);

        using var codec = SKCodec.Create(new MemoryStream(source));

        Assert.NotNull(codec);
        Assert.NotNull(codec!.Info.ColorSpace);
        Assert.False(codec.Info.ColorSpace!.IsSrgb, "the fixture must be tagged with something other than sRGB");
    }

    [Theory]
    [InlineData(ImageOutputFormat.Webp)]
    [InlineData(ImageOutputFormat.Png)]
    public async Task A_display_p3_red_survives_as_red_rather_than_washing_out(ImageOutputFormat format)
    {
        // Omitting the destination colour space does not mean "keep the source's": the codec then
        // performs no transform and the encoder writes no profile, so the P3 numbers land untagged and
        // every viewer reads them as sRGB. Measured on this fixture: #ea3323 without, #ff0000 with.
        var processor = TestProcessor.Build(o =>
        {
            o.Format = format;
            o.Quality = 100;
        });
        await using var source = new MemoryStream(TestImages.WideGamutRedJpeg(40, 20));

        using var result = await processor.ProcessAsync(source);
        using var decoded = SKBitmap.Decode(((MemoryStream)result.Content).ToArray());
        var pixel = decoded.GetPixel(20, 10);

        Assert.True(pixel.Red > 245, $"red channel should be saturated, was {pixel.Red}");
        Assert.True(pixel.Green < 40, $"green channel should be near zero, was {pixel.Green}");
        Assert.True(pixel.Blue < 40, $"blue channel should be near zero, was {pixel.Blue}");
    }
}
