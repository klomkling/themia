using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Themia.Framework.Data.Dapper.Connection;

namespace Themia.Framework.Data.Dapper.SqlServer;

internal sealed class SqlServerConnectionFactory(IConfiguration configuration, IServiceProvider serviceProvider) : IDapperConnectionFactory
{
    // Microsoft.Data.SqlClient maps uniqueidentifier <-> Guid natively, so (unlike MySQL) the resolved
    // (tenant-supplied or "Default") connection string needs no Guid-format normalization.
    public DbConnection Create() => new SqlConnection(DapperConnectionString.Resolve(configuration, serviceProvider));
}
