using SkiaSharp;
using Themia.Imaging.Tests.Fixtures;
using Xunit;

namespace Themia.Imaging.Tests;

/// <summary>
/// Property 1 from coord #0101: <b>metadata is gone.</b> Re-encoding from a decoded pixel buffer drops
/// EXIF structurally — but "structurally" is an argument, not a test, and a listing photo carrying the
/// coordinates of the property and often of the seller's own home is a privacy incident that looks
/// exactly like a working feature.
/// </summary>
public sealed class MetadataTests
{
    [Fact]
    public void The_fixture_really_carries_the_metadata_this_suite_claims_to_strip()
    {
        // Without this, every test below could pass against an input that never had EXIF — the failure
        // mode that leaves a privacy guarantee resting on a fixture nobody checked.
        var source = TestImages.JpegWithExif(60, 40, orientation: 6);

        Assert.True(Contains(source, "Exif"u8), "the fixture must carry an EXIF segment");
        Assert.True(ContainsGpsMarker(source), "the fixture must carry the GPS latitude marker");

        using var codec = SKCodec.Create(new MemoryStream(source));
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedOrigin.RightTop, codec!.EncodedOrigin);
    }

    [Theory]
    [InlineData(ImageOutputFormat.Webp)]
    [InlineData(ImageOutputFormat.Jpeg)]
    [InlineData(ImageOutputFormat.Png)]
    public async Task GPS_coordinates_do_not_survive_processing(ImageOutputFormat format)
    {
        var processor = TestProcessor.Build(o => o.Format = format);
        await using var source = new MemoryStream(TestImages.JpegWithExif(60, 40, orientation: 1));

        using var result = await processor.ProcessAsync(source);
        var bytes = ((MemoryStream)result.Content).ToArray();

        Assert.False(ContainsGpsMarker(bytes), "the GPS latitude must not appear in the output");
        Assert.False(Contains(bytes, "Exif"u8), "no EXIF segment may appear in the output");
    }

    private static bool ContainsGpsMarker(byte[] haystack)
    {
        // The seconds numerator, little-endian, as it is written into the GPS IFD.
        var marker = BitConverter.GetBytes(TestImages.GpsLatitudeSecondsNumerator);
        return Contains(haystack, marker);
    }

    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
