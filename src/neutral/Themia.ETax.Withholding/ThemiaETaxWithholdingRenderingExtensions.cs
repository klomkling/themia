using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Pdf;
using Themia.ETax.Withholding;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the withholding certificate renderer.</summary>
public static class ThemiaETaxWithholdingRenderingExtensions
{
    /// <summary>
    /// Registers <see cref="WithholdingCertificateRenderer"/> and the Puppeteer PDF renderer it prints through
    /// (<c>IVersionedPdfRenderer</c> resolves to it even when the app registers its own <c>IPdfRenderer</c>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurePdf">Optional PDF renderer configuration (Chromium path, concurrency).</param>
    public static IServiceCollection AddThemiaETaxWithholdingRendering(
        this IServiceCollection services, Action<ThemiaPdfOptions>? configurePdf = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddThemiaPdf(configurePdf);
        services.TryAddSingleton<WithholdingCertificateRenderer>();
        return services;
    }
}
