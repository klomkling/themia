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
    /// The decompression-bomb budget: the most decoded pixels <b>a single call</b> will accept, checked
    /// from the codec's header <b>before</b> anything is decoded.
    /// </summary>
    /// <remarks>
    /// An upload byte-limit bounds the <i>encoded</i> size and says nothing about the decoded one. A
    /// solid-colour 12000×12000 PNG is <b>48 bytes</b> on the wire — the fixture in this package's own
    /// tests is exactly that — and decodes to 144 MB; at 30000×30000 it is still tiny on the wire and
    /// decodes to 900 MB. An endpoint with a 10 MB limit looks fully guarded and OOMs the box.
    /// <para>
    /// 100 MP admits every mainstream phone camera (12–64 MP) while refusing the pathological case.
    /// </para>
    /// <para>
    /// It is also the <b>only</b> bound for a format the codec cannot subsample. Skia scales a decode
    /// for JPEG and WebP; a PNG decodes at full size whatever scale is asked for, so at this default a
    /// PNG within budget still costs about 400 MB.
    /// </para>
    /// <para>
    /// <strong>This bounds one decode, not the process — and one decode is not one call.</strong> It is a
    /// per-call check and nothing here counts how many calls are in flight; that is
    /// <see cref="MaxConcurrency"/>. And <c>MaxPixels × 4 bytes</c> is the size of one decoded image, while
    /// a call holds more at its peak: two decoded-size buffers while an EXIF rotation is applied, and the
    /// encoder's working memory on top of one. Measured on a 64 MP image (linux-arm64, SkiaSharp 4.151.1)
    /// with the most detailed input tried, one call peaked at <b>3.8×</b> that size for WebP output,
    /// <b>2.2×</b> for JPEG and <b>2.4×</b> for PNG. So the per-process ceiling is about
    /// <c>MaxPixels × 4 bytes × MaxConcurrency × 3.8</c> at the default WebP output — roughly 3 GB at the
    /// defaults. The package README has the measurements. Size <em>both</em> options from the memory you
    /// are willing to spend, not this one from the camera you expect.
    /// </para>
    /// <para>
    /// <strong>The ceiling uses the <c>MaxPixels</c> in effect for each call.</strong> Options passed to
    /// <see cref="IImageProcessor.ProcessAsync"/> <em>replace</em> the registered options rather than
    /// merging with them — a <c>long</c> has no "unset" value to inherit from — so per-call options that
    /// do not set <c>MaxPixels</c> run that call at this property's default of 100 MP, whatever the
    /// registration says. Copy it from the registered options into every per-call instance.
    /// </para>
    /// </remarks>
    public long MaxPixels { get; set; } = 100_000_000;

    /// <summary>
    /// Maximum number of images allowed to decode at once. Callers beyond the limit queue (honouring
    /// their cancellation token) rather than starting another decode. Default <c>2</c>. Must be at least 1.
    /// </summary>
    /// <remarks>
    /// This is the second half of the memory budget, and without it <see cref="MaxPixels"/> is a
    /// per-decode bound wearing the shape of one. The worst case a process can reach is
    /// <c>MaxPixels × 4 bytes × (measured per-call factor) × MaxConcurrency</c> — the factor is up to 3.8
    /// for WebP output, see <see cref="MaxPixels"/>; supply no bound here and the last term is set by how
    /// many callers happen to arrive together. A browser uploading eight selected photos in parallel is
    /// eight simultaneous requests, not one.
    ///
    /// <para>The default is deliberately small. Decoding is short and bursty, so a low ceiling costs a
    /// little queueing latency in exchange for a predictable memory envelope; raise it once you have
    /// measured a decode's real cost on your host.</para>
    ///
    /// <para><strong>This bound is per process, not per host or per cluster.</strong> It is a
    /// <see cref="System.Threading.SemaphoreSlim"/>, so it knows nothing about other instances. Behind a
    /// load balancer, or when several applications share a host, the real ceiling is
    /// <c>instances × MaxConcurrency × MaxPixels × 4 bytes × the per-call factor</c>. If the goal is
    /// "never let this starve a neighbouring process", set a container memory limit as well — no
    /// in-process bound can protect a process it does not live in, and a cluster-wide decode budget would
    /// need a distributed lease rather than this option.</para>
    ///
    /// <para>Read <b>once</b>, when the processor is constructed: a semaphore's capacity is fixed at
    /// construction, so the per-call <see cref="ImageProcessingOptions"/> that
    /// <see cref="IImageProcessor.ProcessAsync"/> accepts can override size, quality, format and the
    /// pixel budget but <em>not</em> this. The gate belongs to the registration.</para>
    /// </remarks>
    public int MaxConcurrency { get; set; } = 2;

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

        // Zero would not throttle, it would deadlock: every caller would queue on a gate nothing can
        // ever open. That has to fail at boot, not on the first upload.
        if (MaxConcurrency < 1)
        {
            return $"MaxConcurrency must be at least 1, but was {MaxConcurrency}.";
        }

        if (!Enum.IsDefined(typeof(ImageOutputFormat), Format))
        {
            return $"Format {(int)Format} is not a defined ImageOutputFormat.";
        }

        return null;
    }
}
