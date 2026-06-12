using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.EFCore.Abstractions;

namespace Themia.Framework.Data.EFCore.Extensions;

/// <summary>
/// Extension methods for registering Themia data services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Themia DbContext with a custom database provider.
    /// </summary>
    /// <typeparam name="TContext">DbContext type derived from <see cref="ThemiaDbContext"/>.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="provider">Database provider implementation.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="configureOptions">Optional DbContext options configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddThemiaDbContext<TContext>(
        this IServiceCollection services,
        IDatabaseProvider provider,
        IConfiguration configuration,
        Action<DbContextOptionsBuilder>? configureOptions = null)
        where TContext : ThemiaDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(configuration);

        provider.ConfigureServices(services, configuration);

        // Make the active provider discoverable so modules can resolve the app's engine
        // (e.g. Themia.Modules.Scheduling maps it to a MigrationEngine). TryAdd: an app with
        // multiple Themia contexts shares one engine, so the first registration wins.
        services.TryAddSingleton<IDatabaseProvider>(provider);

        // The (serviceProvider, options) overload rebuilds options per scope, so the provider can
        // resolve the request-scoped ITenantAccessor for DB-per-tenant connection routing.
        services.AddDbContext<TContext>((serviceProvider, options) =>
        {
            provider.Configure(options, configuration, serviceProvider);
            configureOptions?.Invoke(options);
        });

        return services;
    }

    /// <summary>
    /// Registers a Themia DbContext with custom options configuration.
    /// </summary>
    /// <typeparam name="TContext">DbContext type derived from <see cref="ThemiaDbContext"/>.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="optionsAction">DbContext options configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddThemiaDbContext<TContext>(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> optionsAction)
        where TContext : ThemiaDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(optionsAction);

        services.AddDbContext<TContext>(optionsAction);

        return services;
    }

}
