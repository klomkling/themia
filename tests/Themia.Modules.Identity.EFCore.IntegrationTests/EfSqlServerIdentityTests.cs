using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.EFCore.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.SqlServer;
using Themia.Modules.Identity.IntegrationTests;
using Themia.Modules.Identity.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Identity.EFCore.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class EfSqlServerIdentityTests(SqlServerIdentityFixture fixture)
    : IdentityStoreConformanceTests, IClassFixture<SqlServerIdentityFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void RegisterIdentity(IServiceCollection services, Action<IdentityModuleOptions> configure)
        => services.AddThemiaIdentityEFCore(configure);

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaSqlServer<TestIdentityDbContext>(configuration);
        services.AddThemiaDataRepositories<TestIdentityDbContext>();
    }
}
