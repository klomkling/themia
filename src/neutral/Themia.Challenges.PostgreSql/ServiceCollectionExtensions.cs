using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Challenges;
using Themia.Challenges.Migrations;
using Themia.Data.Migrations;

namespace Themia.Challenges.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed <c>Themia.Challenges</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresChallengeDialect"/> as the <see cref="IChallengeDialect"/> and runs
    /// the FluentMigrator schema migration immediately so the <c>challenges</c> and
    /// <c>challenge_rate_windows</c> tables exist.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public static IServiceCollection AddThemiaChallengesPostgres(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IChallengeDialect>(new PostgresChallengeDialect(connectionString));

        ThemiaMigrations.Run(MigrationEngine.Postgres, connectionString, typeof(ChallengeSchemaMigration).Assembly);

        return services;
    }
}
