using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.MultiTenancy.Abstractions;

namespace Themia.Framework.Data.EFCore.Infrastructure;

/// <summary>
/// Resolves the connection string for the current scope: the resolved tenant's connection string
/// (DB-per-tenant) when present, otherwise the configured <c>Default</c> connection string. Shared by
/// every Themia EF database provider so the resolution rule cannot drift between engines.
/// </summary>
public static class DatabaseConnectionStringResolver
{
    private const string DefaultConnectionName = "Default";

    /// <summary>
    /// Resolves the connection string: the resolved tenant's connection string when present, otherwise
    /// <c>Default</c>. Throws when neither is available.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="serviceProvider">Scoped service provider used to resolve <see cref="ITenantAccessor"/>.</param>
    /// <returns>The connection string to use for this scope.</returns>
    /// <exception cref="InvalidOperationException">Neither a tenant connection string nor a <c>Default</c> exists.</exception>
    public static string Resolve(IConfiguration configuration, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var tenantConnectionString = serviceProvider.GetService<ITenantAccessor>()?.Current?.ConnectionString;
        if (!string.IsNullOrWhiteSpace(tenantConnectionString))
        {
            return tenantConnectionString;
        }

        var defaultConnectionString = configuration.GetConnectionString(DefaultConnectionName);
        if (string.IsNullOrWhiteSpace(defaultConnectionString))
        {
            throw new InvalidOperationException(
                $"No tenant connection string was resolved and connection string '{DefaultConnectionName}' " +
                "was not found or is empty.");
        }

        return defaultConnectionString;
    }
}
