using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Themia.Framework.Data.Dapper.Connection;
using Themia.MultiTenancy.Abstractions;

namespace Themia.Framework.Data.Dapper.PostgreSql;

internal sealed class NpgsqlConnectionFactory(IConfiguration configuration, IServiceProvider serviceProvider) : IDapperConnectionFactory
{
    private const string DefaultConnectionName = "Default";

    public DbConnection Create() => new NpgsqlConnection(Resolve());

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
