using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Themia.Export.Excel.DependencyInjection;

/// <summary>DI entry point for the ClosedXML Excel backend.</summary>
public static class ExcelExportServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IExcelExporter"/> (stateless singleton).</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddThemiaExcelExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IExcelExporter, ExcelExporter>();
        return services;
    }
}
