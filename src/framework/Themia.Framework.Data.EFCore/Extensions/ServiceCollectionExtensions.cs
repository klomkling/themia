using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.Providers;

namespace Themia.Framework.Data.EFCore.Extensions;

/// <summary>
/// Extension methods for registering Themia data services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Themia DbContext with the PostgreSQL provider.
    /// </summary>
    /// <typeparam name="TContext">DbContext type derived from <see cref="ThemiaDbContext"/>.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="configureOptions">Optional DbContext options configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddThemiaPostgres<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DbContextOptionsBuilder>? configureOptions = null)
        where TContext : ThemiaDbContext
    {
        var provider = new PostgresDatabaseProvider();
        return services.AddThemiaDbContext<TContext>(provider, configuration, configureOptions);
    }

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

    /// <summary>
    /// Registers a database provider by name with automatic discovery.
    /// </summary>
    /// <typeparam name="TContext">DbContext type derived from <see cref="ThemiaDbContext"/>.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="providerName">Provider name (e.g., "postgres").</param>
    /// <param name="configureOptions">Optional DbContext options configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    /// <exception cref="NotSupportedException">Thrown when the provider name is not supported.</exception>
    public static IServiceCollection AddThemiaDbContextWithProvider<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string providerName,
        Action<DbContextOptionsBuilder>? configureOptions = null)
        where TContext : ThemiaDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new ArgumentException("Provider name cannot be null or whitespace.", nameof(providerName));
        }

        var provider = CreateProvider(providerName);
        return services.AddThemiaDbContext<TContext>(provider, configuration, configureOptions);
    }

    private static IDatabaseProvider CreateProvider(string providerName)
    {
        return providerName.ToLowerInvariant() switch
        {
            DatabaseProviderNames.Postgres => new PostgresDatabaseProvider(),
            _ => throw new NotSupportedException(
                $"Database provider '{providerName}' is not supported. " +
                $"Built-in providers: {DatabaseProviderNames.Postgres}. " +
                "For other databases (SQL Server, MySQL, etc.), implement IDatabaseProvider and use AddThemiaDbContext directly.")
        };
    }
}
