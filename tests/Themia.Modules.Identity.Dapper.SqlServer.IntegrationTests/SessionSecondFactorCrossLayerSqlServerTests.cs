using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.SqlServer;
using Themia.Modules.Identity.EFCore.IntegrationTests;
using Themia.Modules.Identity.IntegrationTests;
using Themia.Modules.Identity.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Identity.Dapper.SqlServer.IntegrationTests;

// Dapper's type-handler registry is process-global, so the SQL Server Dapper engine cannot share a test assembly with the
// PostgreSQL one; this class reuses the cross-layer base from the EF Core test project.
[Trait("Category", "Integration")]
public sealed class SessionSecondFactorCrossLayerSqlServerTests(SqlServerIdentityFixture fixture)
    : SessionSecondFactorCrossLayerTests, IClassFixture<SqlServerIdentityFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigureEf(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaSqlServer<TestIdentityDbContext>(configuration);
        services.AddThemiaDataRepositories<TestIdentityDbContext>();
    }

    protected override void ConfigureDapper(IServiceCollection services, IConfiguration configuration) =>
        services.AddThemiaDapperSqlServer(configuration);
}
