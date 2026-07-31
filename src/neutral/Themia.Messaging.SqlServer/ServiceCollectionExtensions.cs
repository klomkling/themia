using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;

namespace Themia.Messaging.SqlServer;

/// <summary>DI entry point for the SQL Server messaging dialects.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server claim, purge, and inbox-admission dialects, resolving the connection
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
    public static IServiceCollection AddThemiaMessagingSqlServer(
        this IServiceCollection services, string connectionStringName = "Default")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        // Maps snake_case columns onto the PascalCase record parameters of ClaimedMessageRow.
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.TryAddSingleton<IOutboxDialect<ClaimedMessageRow>>(sp =>
            new SqlServerMessagingDialect(Resolve(sp, connectionStringName)));

        services.TryAddSingleton<SqlServerMessagingPurgeDialect>();
        services.TryAddSingleton<IOutboxPurgeDialect<ClaimedMessageRow>>(
            sp => sp.GetRequiredService<SqlServerMessagingPurgeDialect>());
        services.TryAddSingleton<IInboxPurgeDialect>(
            sp => sp.GetRequiredService<SqlServerMessagingPurgeDialect>());
        services.TryAddSingleton<IInboxAdmissionDialect, SqlServerInboxAdmission>();

        return services;
    }

    private static string Resolve(IServiceProvider sp, string name)
        => sp.GetRequiredService<IConfiguration>().GetConnectionString(name)
           ?? throw new InvalidOperationException($"Connection string '{name}' was not found.");
}
