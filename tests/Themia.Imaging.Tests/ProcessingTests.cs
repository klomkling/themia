using SkiaSharp;
using Themia.Imaging.Tests.Fixtures;
using Xunit;

namespace Themia.Imaging.Tests;

/// <summary>
/// Property 4 from coord #0101 — <b>never upscales</b> — plus the ordinary shape of the output.
/// </summary>
public sealed class ProcessingTests
{
    [Fact]
    public async Task A_large_image_is_downscaled_to_the_max_edge_with_its_aspect_ratio()
    {
        var processor = TestProcessor.Build();
        await using var source = new MemoryStream(TestImages.Jpeg(2_400, 1_800));

        using var result = await processor.ProcessAsync(source);

        Assert.Equal(1_600, result.Width);
        Assert.Equal(1_200, result.Height);
        Assert.Equal(".webp", result.Extension);
        Assert.True(IsWebp(((MemoryStream)result.Content).ToArray()), "output must be a WebP container");
    }

    [Fact]
    public async Task A_small_image_is_left_alone_rather_than_stretched()
    {
        // Upscaling would make every thumbnail heavier and blurrier while looking like it worked.
        var processor = TestProcessor.Build();
        await using var source = new MemoryStream(TestImages.Jpeg(1_000, 800));

        using var result = await processor.ProcessAsync(source);

        Assert.Equal(1_000, result.Width);
        Assert.Equal(800, result.Height);
    }

    [Fact]
    public async Task A_portrait_image_clamps_its_height()
    {
        var processor = TestProcessor.Build();
        await using var source = new MemoryStream(TestImages.Jpeg(1_200, 3_200));

        using var result = await processor.ProcessAsync(source);

        Assert.Equal(1_600, result.Height);
        Assert.Equal(600, result.Width);
    }

    [Theory]
    [InlineData(ImageOutputFormat.Webp, ".webp")]
    [InlineData(ImageOutputFormat.Jpeg, ".jpg")]
    [InlineData(ImageOutputFormat.Png, ".png")]
    public async Task The_extension_matches_the_bytes_that_were_written(ImageOutputFormat format, string extension)
    {
        // Extension rather than content-type is only safe while the two cannot disagree, so the encoded
        // bytes are decoded back and checked rather than the extension being asserted on its own.
        var processor = TestProcessor.Build(o => o.Format = format);
        await using var source = new MemoryStream(TestImages.Jpeg(200, 100));

        using var result = await processor.ProcessAsync(source);
        var bytes = ((MemoryStream)result.Content).ToArray();

        Assert.Equal(extension, result.Extension);
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        Assert.NotNull(codec);
        Assert.Equal(Expected(format), codec!.EncodedFormat);
    }

    [Fact]
    public async Task The_reported_dimensions_are_the_ones_in_the_encoded_bytes()
    {
        // A caller persists these instead of decoding the result again, so they have to be true.
        var processor = TestProcessor.Build(o => o.MaxEdge = 300);
        await using var source = new MemoryStream(TestImages.Jpeg(1_000, 400));

        using var result = await processor.ProcessAsync(source);
        using var decoded = SKBitmap.Decode(((MemoryStream)result.Content).ToArray());

        Assert.Equal(decoded.Width, result.Width);
        Assert.Equal(decoded.Height, result.Height);
    }

    [Fact]
    public async Task The_content_stream_is_positioned_at_the_start()
    {
        var processor = TestProcessor.Build();
        await using var source = new MemoryStream(TestImages.Jpeg(200, 100));

        using var result = await processor.ProcessAsync(source);

        Assert.Equal(0, result.Content.Position);
        Assert.True(result.Content.Length > 0);
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_are_refused_as_an_argument_problem()
    {
        // ArgumentException, not an InvalidOperation: a corrupt upload is something to tell the person
        // who uploaded it, not a fault to page someone about.
        var processor = TestProcessor.Build();
        await using var garbage = new MemoryStream("this is not an image"u8.ToArray());

        await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(garbage));
    }

    [Fact]
    public async Task Per_call_options_override_the_registered_defaults()
    {
        var processor = TestProcessor.Build(o => o.MaxEdge = 1_600);
        await using var source = new MemoryStream(TestImages.Jpeg(2_000, 1_000));

        using var result = await processor.ProcessAsync(source, new ImageProcessingOptions { MaxEdge = 400 });

        Assert.Equal(400, result.Width);
    }

    [Fact]
    public async Task Per_call_options_are_validated_too()
    {
        // The startup check cannot see a value handed in at the call site, so the call site is checked
        // as well — otherwise a bad Quality reaches the encoder instead of the caller.
        var processor = TestProcessor.Build();
        await using var source = new MemoryStream(TestImages.Jpeg(100, 100));

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => processor.ProcessAsync(source, new ImageProcessingOptions { Quality = 0 }));

        Assert.Contains("Quality", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forward_only_stream_is_accepted()
    {
        // The reason the implementation buffers: a multipart upload stream cannot seek, and the codec
        // needs to.
        var processor = TestProcessor.Build();
        await using var source = new ForwardOnlyStream(TestImages.Jpeg(300, 200));

        using var result = await processor.ProcessAsync(source);

        Assert.Equal(300, result.Width);
    }

    private static SKEncodedImageFormat Expected(ImageOutputFormat format) => format switch
    {
        ImageOutputFormat.Jpeg => SKEncodedImageFormat.Jpeg,
        ImageOutputFormat.Png => SKEncodedImageFormat.Png,
        _ => SKEncodedImageFormat.Webp,
    };

    private static bool IsWebp(byte[] bytes) =>
        bytes.Length > 12
        && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
        && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>A stream that cannot seek, like the one a multipart upload hands over.</summary>
    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
