using System.Data.Common;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Themia.Framework.Data.Dapper.Connection;

namespace Themia.Framework.Data.Dapper.MySql;

internal sealed class MySqlConnectionFactory(IConfiguration configuration, IServiceProvider serviceProvider) : IDapperConnectionFactory
{
    public DbConnection Create()
    {
        // Themia entities use Guid keys stored as CHAR(36); MySqlConnector's default Guid format would
        // otherwise yield phantom-empty Guids. Force Char36 idempotently — applies to both the tenant-supplied
        // and the "Default" connection string resolved by DapperConnectionString.
        var builder = new MySqlConnectionStringBuilder(DapperConnectionString.Resolve(configuration, serviceProvider))
        {
            GuidFormat = MySqlGuidFormat.Char36,
        };
        return new MySqlConnection(builder.ConnectionString);
    }
}
