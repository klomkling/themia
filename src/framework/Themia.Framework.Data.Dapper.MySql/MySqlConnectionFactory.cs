using System.Data.Common;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Themia.Framework.Data.Dapper.Connection;
using Themia.MultiTenancy.Abstractions;

namespace Themia.Framework.Data.Dapper.MySql;

internal sealed class MySqlConnectionFactory(IConfiguration configuration, IServiceProvider serviceProvider) : IDapperConnectionFactory
{
    private const string DefaultConnectionName = "Default";

    public DbConnection Create()
    {
        // Themia entities use Guid keys stored as CHAR(36); MySqlConnector's default Guid format would
        // otherwise yield phantom-empty Guids. Force Char36 idempotently.
        var builder = new MySqlConnectionStringBuilder(Resolve()) { GuidFormat = MySqlGuidFormat.Char36 };
        return new MySqlConnection(builder.ConnectionString);
    }

    private string Resolve()
    {
        var tenantCs = (serviceProvider.GetService(typeof(ITenantAccessor)) as ITenantAccessor)?.Current?.ConnectionString;
        if (!string.IsNullOrWhiteSpace(tenantCs)) return tenantCs;

        var cs = configuration.GetConnectionString(DefaultConnectionName);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"No tenant connection string was resolved and connection string '{DefaultConnectionName}' was not found or is empty.");

        return cs;
    }
}
