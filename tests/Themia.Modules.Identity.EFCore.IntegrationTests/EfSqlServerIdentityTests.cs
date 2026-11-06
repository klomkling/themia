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

    [Fact] // EF only: the Dapper unit of work keeps its own instance-version residual (see CHANGELOG)
    public Task A_rolled_back_unit_of_work_does_not_leave_the_held_instance_ahead_of_its_row()
        => Re_saving_a_held_instance_after_a_rolled_back_unit_of_work_conflicts_with_the_winner();

    protected override void RegisterIdentity(IServiceCollection services, Action<IdentityModuleOptions> configure)
        => services.AddThemiaIdentityEFCore(configure);

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaSqlServer<TestIdentityDbContext>(configuration);
        services.AddThemiaDataRepositories<TestIdentityDbContext>();
    }
}
