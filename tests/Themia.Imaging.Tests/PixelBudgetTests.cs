using SkiaSharp;
using Themia.Imaging.Tests.Fixtures;
using Xunit;

namespace Themia.Imaging.Tests;

/// <summary>
/// Property 3 from coord #0101: <b>the pixel budget is checked before decoding, not after.</b> A test
/// asserting "a 12000×12000 PNG is rejected" passes either way; these assert it is rejected
/// <i>without allocating</i>, which is the only thing that makes the guard worth having.
/// </summary>
/// <remarks>
/// Not run in parallel with anything else: one test measures the process working set, and a neighbour
/// decoding an image at the same moment would move it.
/// </remarks>
[Collection(nameof(PixelBudgetTests))]
[CollectionDefinition(nameof(PixelBudgetTests), DisableParallelization = true)]
public sealed class PixelBudgetTests
{
    [Fact]
    public void The_bomb_fixture_is_tiny_on_the_wire_and_enormous_decoded()
    {
        // The premise the whole guard rests on, asserted rather than assumed: an upload byte-limit sees
        // this file as 48 bytes.
        var bomb = TestImages.HeaderOnlyPng(12_000, 12_000);

        Assert.True(bomb.Length < 100, $"the fixture must be tiny on the wire, was {bomb.Length} bytes");

        using var codec = SKCodec.Create(new MemoryStream(bomb));
        Assert.NotNull(codec);
        Assert.Equal(12_000, codec!.Info.Width);
        Assert.Equal(12_000, codec.Info.Height);
        Assert.Equal(144_000_000, (long)codec.Info.Width * codec.Info.Height * codec.Info.BytesPerPixel);
    }

    [Fact]
    public async Task An_over_budget_image_is_refused_with_its_dimensions_and_the_limit()
    {
        var processor = TestProcessor.Build();
        await using var bomb = new MemoryStream(TestImages.HeaderOnlyPng(12_000, 12_000));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(bomb));

        // Named, because whoever reads this in a log needs to know which upload and by how much.
        Assert.Contains("12000x12000", error.Message, StringComparison.Ordinal);
        Assert.Contains("100,000,000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_refusal_happens_before_the_decode_allocates()
    {
        // 30000x30000 = 900 MP, nine times over the budget, and Skia decodes it perfectly happily —
        // measured at ~900 MB of working set in about 60 ms. So if the guard sat AFTER the decode, this
        // call would still throw and still pass a message assertion; only the memory tells them apart.
        var processor = TestProcessor.Build();
        await using var bomb = new MemoryStream(TestImages.HeaderOnlyPng(30_000, 30_000));

        var before = Environment.WorkingSet;
        await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(bomb));
        var growth = Environment.WorkingSet - before;

        Assert.True(
            growth < 200L * 1024 * 1024,
            $"the guard must refuse before decoding; working set grew {growth / 1024 / 1024} MB, " +
            "and an unguarded decode of this fixture allocates about 900 MB");
    }

    [Fact]
    public async Task A_raised_budget_admits_what_the_default_refuses()
    {
        // Proves the guard reads the option rather than a constant — the shape the source implementation
        // had, and the reason this is a package rather than a copy.
        var processor = TestProcessor.Build(o =>
        {
            o.MaxPixels = 200_000_000;
            o.MaxEdge = 100;
        });
        await using var bomb = new MemoryStream(TestImages.HeaderOnlyPng(12_000, 12_000));

        using var result = await processor.ProcessAsync(bomb);

        Assert.Equal(100, Math.Max(result.Width, result.Height));
    }

    [Theory]
    [InlineData(8_000, 6_000, false)]     // 48 MP — a real high-end phone photo, allowed
    [InlineData(10_000, 10_000, false)]   // exactly at the budget
    [InlineData(10_000, 10_001, true)]    // one row over it
    [InlineData(12_000, 12_000, true)]    // 144 MP
    [InlineData(60_000, 60_000, true)]    // 3.6 billion — overflows int, which is why the guard uses long
    public void The_budget_is_evaluated_in_long_arithmetic(int width, int height, bool expected)
    {
        Assert.Equal(expected, SkiaImageProcessor.ExceedsPixelBudget(width, height, 100_000_000));
    }

    [Theory]
    [InlineData(1_000, 1.0f)]    // already within the target: full decode
    [InlineData(3_000, 1.0f)]    // 3000/2 = 1500 < 1600: full decode
    [InlineData(3_200, 0.5f)]    // 3200/2 = 1600: half
    [InlineData(6_400, 0.25f)]   // 6400/4 = 1600: quarter
    [InlineData(10_000, 0.25f)]  // 10000/8 = 1250 < 1600, so a quarter is as far as it goes
    public void The_subsample_keeps_the_long_edge_at_or_above_the_target(int longestEdge, float expected)
    {
        Assert.Equal(expected, SkiaImageProcessor.SubsampleScale(longestEdge, 1_600));
    }
}
