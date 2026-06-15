using System.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Dapper;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.DependencyInjection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;

/// <summary>DI registration for the Themia Dapper data layer on SQL Server.</summary>
public static class SqlServerDapperServiceCollectionExtensions
{
    /// <summary>Registers the Themia Dapper data layer on SQL Server. The connection string is resolved per
    /// scope from <c>ITenantAccessor.Current?.ConnectionString</c>, falling back to the "Default" connection
    /// string. Audit timestamps round-trip via a <c>datetime2</c> <see cref="System.DateTimeOffset"/> handler.</summary>
    public static IServiceCollection AddThemiaDapperSqlServer(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DapperDataOptions>? configure = null)
    {
        DapperConfiguration.ConfigureEngine("SQL Server", DbType.DateTime2);
        services.AddThemiaDapperCore(configure);
        services.AddScoped<IDapperConnectionFactory>(sp => new SqlServerConnectionFactory(configuration, sp));
        services.AddSingleton<ISqlCompiler, SqlServerSqlCompiler>();
        // SqlClient does not surface a usable SqlState, so replace the default SQLSTATE interpreter with one
        // that matches SQL Server's native duplicate-key error numbers (2627 / 2601).
        services.Replace(ServiceDescriptor.Singleton<ISqlExceptionInterpreter, SqlServerSqlExceptionInterpreter>());
        return services;
    }
}
