using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.DependencyInjection;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.MySql.DependencyInjection;

/// <summary>DI registration for the Themia Dapper data layer on MySQL.</summary>
public static class MySqlDapperServiceCollectionExtensions
{
    /// <summary>Registers the Themia Dapper data layer on MySQL. The connection string is resolved per scope
    /// from <c>ITenantAccessor.Current?.ConnectionString</c>, falling back to the "Default" connection string.
    /// <c>GuidFormat=Char36</c> is enforced for Guid keys.</summary>
    public static IServiceCollection AddThemiaDapperMySql(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DapperDataOptions>? configure = null)
    {
        MySqlDapperConfiguration.EnsureConfigured();
        services.AddThemiaDapperCore(configure);
        services.AddScoped<IDapperConnectionFactory>(sp => new MySqlConnectionFactory(configuration, sp));
        services.AddSingleton<ISqlCompiler, MySqlSqlCompiler>();
        return services;
    }
}
