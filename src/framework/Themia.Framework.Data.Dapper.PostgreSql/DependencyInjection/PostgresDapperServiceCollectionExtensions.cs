using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.DependencyInjection;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.PostgreSql.DependencyInjection;

/// <summary>DI registration for the Themia Dapper data layer on PostgreSQL.</summary>
public static class PostgresDapperServiceCollectionExtensions
{
    /// <summary>Registers the Themia Dapper data layer on PostgreSQL. The connection string is resolved per
    /// scope from <c>ITenantAccessor.Current?.ConnectionString</c>, falling back to the "Default"
    /// connection string.</summary>
    public static IServiceCollection AddThemiaDapperPostgres(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DapperDataOptions>? configure = null)
    {
        services.AddThemiaDapperCore(configure);
        services.AddScoped<IDapperConnectionFactory>(sp => new NpgsqlConnectionFactory(configuration, sp));
        services.AddSingleton<ISqlCompiler, PostgresSqlCompiler>();
        return services;
    }
}
