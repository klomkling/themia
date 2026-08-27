using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Themia.Imaging.Tests.Fixtures;
using Xunit;

namespace Themia.Imaging.Tests;

public sealed class AddThemiaImagingTests
{
    [Fact]
    public void Registers_the_processor_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddThemiaImaging();

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IImageProcessor>();
        Assert.IsType<SkiaImageProcessor>(first);
        Assert.Same(first, provider.GetRequiredService<IImageProcessor>());
    }

    [Fact]
    public void A_processor_the_caller_registered_first_wins()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IImageProcessor, ThrowingProcessor>();
        services.AddThemiaImaging();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<ThrowingProcessor>(provider.GetRequiredService<IImageProcessor>());
    }

    [Fact]
    public async Task Configured_options_reach_the_processor()
    {
        var services = new ServiceCollection();
        services.AddThemiaImaging(o => o.MaxEdge = 240);

        using var provider = services.BuildServiceProvider();
        var processor = provider.GetRequiredService<IImageProcessor>();

        using var result = await processor.ProcessAsync(new MemoryStream(TestImages.Jpeg(1_000, 500)));

        Assert.Equal(240, result.Width);
    }

    [Fact]
    public async Task Bad_options_fail_the_host_at_startup_and_name_the_value()
    {
        // Without ValidateOnStart this surfaces on somebody's first upload instead of at boot.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddThemiaImaging(o => o.Quality = 0);

        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Quality", string.Join(" ", error.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_options_start_the_host()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddThemiaImaging(o => o.Format = ImageOutputFormat.Jpeg);

        using var host = builder.Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    private sealed class ThrowingProcessor : IImageProcessor
    {
        public Task<ProcessedImage> ProcessAsync(
            Stream source, ImageProcessingOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
