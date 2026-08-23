using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Themia.Data.Probes;
using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;

namespace Themia.Messaging.PostgreSql;

/// <summary>DI entry point for the PostgreSQL messaging dialects.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL claim and purge dialects, resolving the connection string from
    /// <c>ConnectionStrings:<paramref name="connectionStringName"/></c> at first use.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionStringName">
    /// Name of the connection string the dialects use. Defaults to <c>"Default"</c>, matching
    /// <c>MessagingModuleOptions.ConnectionStringName</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionStringName"/> is null or whitespace.</exception>
    public static IServiceCollection AddThemiaMessagingPostgreSql(
        this IServiceCollection services, string connectionStringName = "Default")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        // Maps snake_case columns onto the PascalCase record parameters of ClaimedMessageRow.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.TryAddSingleton<IOutboxDialect<ClaimedMessageRow>>(sp =>
            new PostgresMessagingDialect(Resolve(sp, connectionStringName)));

        services.TryAddSingleton<PostgresMessagingPurgeDialect>();
        services.TryAddSingleton<IOutboxPurgeDialect<ClaimedMessageRow>>(
            sp => sp.GetRequiredService<PostgresMessagingPurgeDialect>());
        services.TryAddSingleton<IInboxPurgeDialect>(
            sp => sp.GetRequiredService<PostgresMessagingPurgeDialect>());
        services.TryAddSingleton<IInboxAdmissionDialect, PostgresInboxAdmission>();

        // Resolved from IServiceProvider, not captured: this package takes a connection string NAME and
        // reads the value from IConfiguration, exactly as the dialects above do via Resolve(...).
        // Named after this package, not Themia.Modules.Messaging: unlike the other probe registrations
        // (each in a package that references the one owning its migration), this package does not
        // reference Themia.Modules.Messaging at all, so naming the diagnosis after it would point a
        // debugging operator at a package this one has no structural relationship to.
        services.AddPostgresSchemaProbe(
            "Themia.Messaging.PostgreSql",
            sp =>
            {
                var connection = new NpgsqlConnection(Resolve(sp, connectionStringName));
                connection.Open();
                return connection;
            },
            ["messaging_outbox_messages", "messaging_inbox_messages"]);

        return services;
    }

    private static string Resolve(IServiceProvider sp, string name)
        => sp.GetRequiredService<IConfiguration>().GetConnectionString(name)
           ?? throw new InvalidOperationException($"Connection string '{name}' was not found.");
}
