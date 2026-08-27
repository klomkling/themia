using Microsoft.Extensions.Options;

namespace Themia.Imaging.Tests;

internal static class TestProcessor
{
    internal static SkiaImageProcessor Build(Action<ImageProcessingOptions>? configure = null)
    {
        var options = new ImageProcessingOptions();
        configure?.Invoke(options);
        return new SkiaImageProcessor(Options.Create(options));
    }
}
