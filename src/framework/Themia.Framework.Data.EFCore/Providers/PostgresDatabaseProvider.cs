using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.MultiTenancy.Abstractions;

namespace Themia.Framework.Data.EFCore.Providers;

/// <summary>
/// PostgreSQL database provider using the Npgsql EF Core provider. Routes to the per-tenant
/// connection string when the resolved <see cref="ITenantAccessor.Current"/> carries one
/// (DB-per-tenant), otherwise falls back to the <c>Default</c> connection string (shared DB +
/// the global tenant query filter).
/// </summary>
public sealed class PostgresDatabaseProvider : IDatabaseProvider
{
    private const string DefaultConnectionName = "Default";

    /// <inheritdoc />
    public string ProviderName => DatabaseProviderNames.Postgres;

    /// <inheritdoc />
    public void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var connectionString = ResolveConnectionString(configuration, serviceProvider);

        optionsBuilder
            .UseNpgsql(connectionString, ConfigureNpgsqlOptions)
            .UseSnakeCaseNamingConvention();
    }

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // No eager connection-string validation: with DB-per-tenant the connection string may be
        // supplied at request time via ITenantAccessor.Current.ConnectionString, so a missing
        // "Default" is not necessarily a misconfiguration. Resolution + validation happen per scope
        // in Configure (which throws if neither a tenant nor a Default connection string exists).
    }

    private static void ConfigureNpgsqlOptions(NpgsqlDbContextOptionsBuilder builder)
    {
        // Deliberately NOT calling EnableRetryOnFailure(): a retrying execution strategy forbids
        // user-initiated transactions (BeginTransactionAsync throws "does not support user-initiated
        // transactions"). IUnitOfWork.BeginTransactionAsync is a published, first-class contract, so the
        // retrying strategy and that contract are mutually exclusive in EF Core. Callers that want
        // automatic retry should use IUnitOfWork.ExecuteInTransactionAsync, which wraps the work in the
        // provider's execution strategy explicitly.
    }

    /// <summary>
    /// Resolves the connection string for the current scope: the resolved tenant's connection string
    /// when present, otherwise the configured <c>Default</c>. Throws when neither is available.
    /// </summary>
    internal static string ResolveConnectionString(IConfiguration configuration, IServiceProvider serviceProvider)
    {
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
