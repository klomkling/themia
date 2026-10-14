using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.ETax;
using Themia.Pdf;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI registration for Themia.ETax rendering.</summary>
public static class ThemiaETaxServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TaxDocumentRenderer"/> and the Themia.Pdf services it prints with
    /// (<see cref="IVersionedPdfRenderer"/>). Idempotent.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configurePdf">Optional configuration of <see cref="ThemiaPdfOptions"/>, e.g. a Chromium path.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaETaxRendering(
        this IServiceCollection services, Action<ThemiaPdfOptions>? configurePdf = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddThemiaPdf(configurePdf);
        services.TryAddSingleton<TaxDocumentRenderer>();
        return services;
    }
}
