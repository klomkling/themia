using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.Infrastructure;

namespace Themia.Framework.Data.EFCore.SqlServer;

/// <summary>
/// SQL Server database provider. Routes to the per-tenant connection string when the resolved tenant
/// carries one (DB-per-tenant), otherwise the <c>Default</c> connection string (shared DB + the global
/// tenant query filter).
/// </summary>
/// <remarks>
/// Themia's framework columns are explicitly snake_case (mapped in <c>ThemiaDbContext</c>); the
/// adopter's own columns follow EF defaults (PascalCase on SQL Server). To snake_case the whole model,
/// reference <c>EFCore.NamingConventions</c> and apply it via the registration delegate:
/// <c>AddThemiaSqlServer&lt;TContext&gt;(config, configureOptions: o =&gt; o.UseSnakeCaseNamingConvention())</c>.
/// </remarks>
public sealed class SqlServerDatabaseProvider : IDatabaseProvider
{
    /// <inheritdoc />
    public string ProviderName => DatabaseProviderNames.SqlServer;

    /// <inheritdoc />
    public void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var connectionString = DatabaseConnectionStringResolver.Resolve(configuration, serviceProvider);

        optionsBuilder.UseSqlServer(connectionString, ConfigureSqlServerOptions);
    }

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // No eager connection-string validation: with DB-per-tenant the connection string may be supplied
        // at request time via ITenantAccessor.Current.ConnectionString. Resolution + validation happen per
        // scope in Configure.

        // SqlClient does not surface a usable SqlState, so replace the default SQLSTATE interpreter with one
        // that matches SQL Server's native duplicate-key error numbers (2627 / 2601).
        services.Replace(ServiceDescriptor.Singleton<ISqlExceptionInterpreter, SqlServerSqlExceptionInterpreter>());
    }

    private static void ConfigureSqlServerOptions(SqlServerDbContextOptionsBuilder builder)
    {
        // Automatic transient-fault retry (EnableRetryOnFailure) is intentionally NOT configured: a
        // retrying execution strategy is incompatible with the user-initiated transactions exposed by
        // IUnitOfWork.BeginTransactionAsync (EF throws on BeginTransaction under a retrying strategy).
        // Hosts that need transient-fault resilience and do NOT use manual transactions can re-enable it
        // via the configureOptions delegate of AddThemiaSqlServer.
    }
}
