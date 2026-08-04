using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;

namespace Themia.Messaging.MySql;

/// <summary>DI entry point for the MySQL messaging dialects.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MySQL claim, purge, and inbox-admission dialects, resolving the connection
    /// string from <c>ConnectionStrings:<paramref name="connectionStringName"/></c> at first use.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionStringName">
    /// Name of the connection string the dialects use. Defaults to <c>"Default"</c>, matching
    /// <c>MessagingModuleOptions.ConnectionStringName</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionStringName"/> is null or whitespace.</exception>
    public static IServiceCollection AddThemiaMessagingMySql(
        this IServiceCollection services, string connectionStringName = "Default")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        // Maps snake_case columns onto the PascalCase record parameters of ClaimedMessageRow.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.TryAddSingleton<IOutboxDialect<ClaimedMessageRow>>(sp =>
            new MySqlMessagingDialect(Resolve(sp, connectionStringName)));

        services.TryAddSingleton<MySqlMessagingPurgeDialect>();
        services.TryAddSingleton<IOutboxPurgeDialect<ClaimedMessageRow>>(
            sp => sp.GetRequiredService<MySqlMessagingPurgeDialect>());
        services.TryAddSingleton<IInboxPurgeDialect>(
            sp => sp.GetRequiredService<MySqlMessagingPurgeDialect>());
        services.TryAddSingleton<IInboxAdmissionDialect, MySqlInboxAdmission>();

        return services;
    }

    private static string Resolve(IServiceProvider sp, string name)
        => sp.GetRequiredService<IConfiguration>().GetConnectionString(name)
           ?? throw new InvalidOperationException($"Connection string '{name}' was not found.");
}
