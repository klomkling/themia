using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Themia.Framework.Data.Dapper.Connection;

namespace Themia.Framework.Data.Dapper.PostgreSql;

internal sealed class NpgsqlConnectionFactory(IConfiguration configuration, IServiceProvider serviceProvider) : IDapperConnectionFactory
{
    public DbConnection Create() => new NpgsqlConnection(DapperConnectionString.Resolve(configuration, serviceProvider));
}
