using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Themia.Imaging;

/// <summary>Registers <see cref="IImageProcessor"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers image processing with the given defaults.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional overrides for size, quality, format and the pixel budget.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// <b>This package references managed SkiaSharp only.</b> The host adds the native asset for the
    /// RID it runs on — <c>SkiaSharp.NativeAssets.Linux</c> in a Linux container,
    /// <c>SkiaSharp.NativeAssets.macos</c> for local development. That split is deliberate: shipping one
    /// RID's binaries from a neutral package would force them on every consumer, and the failure mode it
    /// creates — works on a developer's Mac, fails in the container — is the one worth designing out.
    /// </remarks>
    public static IServiceCollection AddThemiaImaging(
        this IServiceCollection services, Action<ImageProcessingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<ImageProcessingOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        // ValidateOnStart rather than only the constructor: a bad Quality or MaxEdge should fail the
        // boot, not somebody's first upload.
        optionsBuilder.ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ImageProcessingOptions>, ImageProcessingOptionsValidator>());

        // Singleton: the processor holds no per-request state and the codec is thread-safe per call.
        services.TryAddSingleton<IImageProcessor, SkiaImageProcessor>();

        return services;
    }
}
