using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Themia.Content.Internal;

namespace Themia.Content.DependencyInjection;

/// <summary>DI entry point for <c>Themia.Content</c>.</summary>
public static class ContentServiceCollectionExtensions
{
    /// <summary>
    /// Registers validated <see cref="ContentOptions"/>, <see cref="TimeProvider.System"/> (unless one is registered),
    /// logging, and <see cref="IContentPageService"/>. Does not register a dialect — call exactly one of
    /// <c>AddThemiaContentPostgres</c>, <c>AddThemiaContentMySql</c> or <c>AddThemiaContentSqlServer</c>, in either order.
    /// </summary>
    /// <remarks>The dialect is checked when <see cref="IContentPageService"/> is first resolved, not here, so every
    /// registration order works and a missing engine still fails loudly.</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the languages and the fallback language.</param>
    /// <exception cref="InvalidOperationException">The configured options cannot serve a page.</exception>
    public static IServiceCollection AddThemiaContent(this IServiceCollection services, Action<ContentOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ContentOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();

        services.TryAddSingleton<IContentPageService>(provider => new ContentPageService(
            provider.GetService<IContentPageDialect>() ?? throw new InvalidOperationException(
                "No IContentPageDialect is registered. Call AddThemiaContentPostgres, AddThemiaContentMySql or " +
                "AddThemiaContentSqlServer as well as AddThemiaContent."),
            provider.GetRequiredService<ContentOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ContentPageService>>()));

        return services;
    }
}
