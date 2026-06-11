using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore;
using Themia.Framework.Data.EFCore.Extensions;

namespace Themia.Framework.Data.EFCore.PostgreSql;

/// <summary>
/// Registration extensions for the Themia PostgreSQL EF Core provider.
/// </summary>
public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Themia <see cref="ThemiaDbContext"/> with the PostgreSQL provider.
    /// </summary>
    /// <typeparam name="TContext">DbContext type derived from <see cref="ThemiaDbContext"/>.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="useGlobalSnakeCaseNaming">
    /// When <c>true</c>, snake_cases the entire model (legacy). Default <c>false</c>: only framework columns
    /// are snake_case; adopter columns follow EF defaults.
    /// </param>
    /// <param name="configureOptions">Optional DbContext options configuration.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddThemiaPostgres<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useGlobalSnakeCaseNaming = false,
        Action<DbContextOptionsBuilder>? configureOptions = null)
        where TContext : ThemiaDbContext
    {
        var provider = new PostgresDatabaseProvider(useGlobalSnakeCaseNaming);
        return services.AddThemiaDbContext<TContext>(provider, configuration, configureOptions);
    }
}
