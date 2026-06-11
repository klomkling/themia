using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.Infrastructure;

namespace Themia.Framework.Data.EFCore.PostgreSql;

/// <summary>
/// PostgreSQL database provider using the Npgsql EF Core provider. Routes to the per-tenant connection
/// string when the resolved tenant carries one (DB-per-tenant), otherwise the <c>Default</c> connection
/// string (shared DB + the global tenant query filter).
/// </summary>
/// <param name="useGlobalSnakeCaseNaming">
/// When <c>true</c>, applies <c>UseSnakeCaseNamingConvention()</c> to the whole model (legacy behavior,
/// snake_cases the adopter's own columns too). Default <c>false</c>: only Themia's framework columns are
/// snake_case (mapped explicitly in <c>ThemiaDbContext</c>); the adopter's columns follow EF defaults.
/// </param>
public sealed class PostgresDatabaseProvider(bool useGlobalSnakeCaseNaming = false) : IDatabaseProvider
{
    /// <inheritdoc />
    public string ProviderName => DatabaseProviderNames.Postgres;

    /// <inheritdoc />
    public void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var connectionString = DatabaseConnectionStringResolver.Resolve(configuration, serviceProvider);

        optionsBuilder.UseNpgsql(connectionString, ConfigureNpgsqlOptions);

        if (useGlobalSnakeCaseNaming)
        {
            optionsBuilder.UseSnakeCaseNamingConvention();
        }
    }

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // No eager connection-string validation: with DB-per-tenant the connection string may be supplied
        // at request time via ITenantAccessor.Current.ConnectionString, so a missing "Default" is not
        // necessarily a misconfiguration. Resolution + validation happen per scope in Configure.
    }

    private static void ConfigureNpgsqlOptions(NpgsqlDbContextOptionsBuilder builder)
    {
        // Automatic transient-fault retry (EnableRetryOnFailure) is intentionally NOT configured: a
        // retrying execution strategy is incompatible with the user-initiated transactions exposed by
        // IUnitOfWork.BeginTransactionAsync (EF throws on BeginTransaction under a retrying strategy).
        // Hosts that need transient-fault resilience and do NOT use manual transactions can re-enable it
        // via the configureOptions delegate of AddThemiaPostgres, accepting that BeginTransactionAsync
        // will then throw.
    }
}
