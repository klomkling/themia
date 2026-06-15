using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;
using Themia.Modules.Identity.AspNetCore.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.IntegrationTests;

/// <summary>JWT auth-flow integration tests: Dapper + SQL Server peer.</summary>
[Trait("Category", "Integration")]
public sealed class DapperSqlServerAuthTests(SqlServerAuthFixture fixture)
    : AuthFlowConformanceTests, IClassFixture<SqlServerAuthFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;

    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaDapperSqlServer(configuration);
    }
}
