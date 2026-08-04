using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Challenges;
using Themia.Challenges.Migrations;
using Themia.Data.Migrations;

namespace Themia.Challenges.SqlServer;

/// <summary>DI entry point for the SQL Server-backed <c>Themia.Challenges</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SqlServerChallengeDialect"/> as the <see cref="IChallengeDialect"/> and
    /// runs the FluentMigrator schema migration immediately so the <c>challenges</c> and
    /// <c>challenge_rate_windows</c> tables exist.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    public static IServiceCollection AddThemiaChallengesSqlServer(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IChallengeDialect>(new SqlServerChallengeDialect(connectionString));

        ThemiaMigrations.Run(MigrationEngine.SqlServer, connectionString, typeof(ChallengeSchemaMigration).Assembly);

        return services;
    }
}
