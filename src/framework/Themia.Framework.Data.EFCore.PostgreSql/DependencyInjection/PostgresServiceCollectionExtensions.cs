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
    /// <param name="configureOptions">
    /// Optional DbContext options configuration — e.g. apply a global naming convention here
    /// (<c>o =&gt; o.UseSnakeCaseNamingConvention()</c>, with your own <c>EFCore.NamingConventions</c>
    /// reference) to restore legacy whole-model snake_case.
    /// </param>
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
}
