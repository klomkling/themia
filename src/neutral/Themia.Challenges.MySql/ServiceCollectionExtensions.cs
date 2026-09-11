using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Challenges;
using Themia.Challenges.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;

namespace Themia.Challenges.MySql;

/// <summary>DI entry point for the MySQL-backed <c>Themia.Challenges</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MySqlChallengeDialect"/> as the <see cref="IChallengeDialect"/> and runs
    /// the FluentMigrator schema migration immediately so the <c>challenges</c> and
    /// <c>challenge_rate_windows</c> tables exist.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">MySQL connection string.</param>
    /// <remarks>
    /// Resolves <see cref="ILoggerFactory"/> from <paramref name="services"/> at first
    /// <see cref="IChallengeDialect"/> resolution (via a factory registration, not eagerly — the same
    /// reason <c>AddThemiaChallenges</c>'s own dialect guard is lazy: this can run before or after the
    /// host's logging registration depending on call order) so <c>DeadlockRetryingConnection</c>'s
    /// retry activity is diagnosable. Falls back to <see cref="NullLoggerFactory"/> when no logging
    /// pipeline is registered, rather than throwing — logging is a diagnostic aid here, not a
    /// requirement to construct the dialect.
    /// </remarks>
    public static IServiceCollection AddThemiaChallengesMySql(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IChallengeDialect>(sp =>
            new MySqlChallengeDialect(connectionString, sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance));

        // Register on use, so an adopter who calls only engine-specific methods still has the enum path
        // (a module, SchedulingSchema.Migrate) resolvable — see MigrationEngineRegistry.Add (coord #0126).
        MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter);

        ThemiaMigrations.Run(MySqlMigrationEngine.Adapter, connectionString, typeof(ChallengeSchemaMigration).Assembly);

        return services;
    }
}
