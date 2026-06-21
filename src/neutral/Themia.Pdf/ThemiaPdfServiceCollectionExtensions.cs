using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Pdf;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI registration for the Themia PDF rendering core.</summary>
public static class ThemiaPdfServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IHtmlTemplateRenderer"/> and <see cref="IPdfRenderer"/> as singletons,
    /// along with a configured <see cref="ThemiaPdfOptions"/>. Idempotent.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="ThemiaPdfOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaPdf(
        this IServiceCollection services,
        Action<ThemiaPdfOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ThemiaPdfOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IHtmlTemplateRenderer, HandlebarsHtmlTemplateRenderer>();
        services.TryAddSingleton<IPdfRenderer, PuppeteerPdfRenderer>();

        return services;
    }
}
