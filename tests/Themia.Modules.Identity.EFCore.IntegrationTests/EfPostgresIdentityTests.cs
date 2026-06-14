using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Identity.IntegrationTests;
using Themia.Modules.Identity.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Identity.EFCore.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class EfPostgresIdentityTests(PostgresIdentityFixture fixture)
    : IdentityStoreConformanceTests, IClassFixture<PostgresIdentityFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaPostgres<TestIdentityDbContext>(configuration);
        services.AddThemiaDataRepositories<TestIdentityDbContext>();
    }
}
