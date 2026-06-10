using System.Data.Common;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Themia.Framework.Data.Dapper.Connection;

namespace Themia.Framework.Data.Dapper.MySql;

internal sealed class MySqlConnectionFactory(IConfiguration configuration, IServiceProvider serviceProvider) : IDapperConnectionFactory
{
    public DbConnection Create()
    {
        // Themia entities use Guid keys stored as CHAR(36). Pin Char36 on the builder, overriding any
        // GuidFormat/OldGuids in the resolved (tenant-supplied or "Default") connection string — a caller flag
        // that disagreed with the column would silently corrupt Guid lookups (every by-Guid query matching 0
        // rows). OldGuids is cleared first because OldGuids and GuidFormat are mutually exclusive in MySqlConnector.
        var builder = new MySqlConnectionStringBuilder(DapperConnectionString.Resolve(configuration, serviceProvider))
        {
            OldGuids = false,
            GuidFormat = MySqlGuidFormat.Char36,
        };
        return new MySqlConnection(builder.ConnectionString);
    }
}
