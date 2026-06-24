using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Export.Csv;

namespace Themia.Export.DependencyInjection;

/// <summary>DI entry point for the neutral export contract.</summary>
public static class ExportServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ICsvExporter"/> (stateless singleton).</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddThemiaExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICsvExporter, CsvExporter>();
        return services;
    }
}
