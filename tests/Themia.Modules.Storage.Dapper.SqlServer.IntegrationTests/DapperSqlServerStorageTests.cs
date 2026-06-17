using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;
using Themia.Modules.Storage.IntegrationTests;
using Themia.Modules.Storage.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Storage.Dapper.SqlServer.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class DapperSqlServerStorageTests(SqlServerStorageFixture fixture)
    : StorageConformanceTests, IClassFixture<SqlServerStorageFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaDapperSqlServer(configuration);
    }
}
