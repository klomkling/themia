using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Modules.Notifications.Outbox;

namespace Themia.Modules.Notifications.SqlServer;

/// <summary>DI entry point for the SQL Server outbox-claim dialect.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQL Server <see cref="INotificationsSqlDialect"/>, resolving its connection string
    /// from <c>ConnectionStrings:<paramref name="connectionStringName"/></c> at first use.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionStringName">
    /// Name of the connection string (in <c>ConnectionStrings</c>) the dialect drains. Defaults to
    /// <c>"Default"</c>, matching <c>NotificationsModuleOptions.ConnectionStringName</c>.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="connectionStringName"/> is null or whitespace.</exception>
    public static IServiceCollection AddThemiaNotificationsSqlServer(
        this IServiceCollection services, string connectionStringName = "Default")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringName);

        services.TryAddSingleton<INotificationsSqlDialect>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var connectionString = configuration.GetConnectionString(connectionStringName)
                ?? throw new InvalidOperationException(
                    $"Connection string '{connectionStringName}' was not found.");
            return new SqlServerNotificationsDialect(connectionString);
        });

        return services;
    }
}
