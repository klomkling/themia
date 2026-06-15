using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Identity.AspNetCore.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.IntegrationTests;

/// <summary>JWT auth-flow integration tests: EF Core + PostgreSQL peer.</summary>
[Trait("Category", "Integration")]
public sealed class EfPostgresAuthTests(PostgresAuthFixture fixture)
    : AuthFlowConformanceTests, IClassFixture<PostgresAuthFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;

    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaPostgres<TestIdentityDbContext>(configuration);
        services.AddThemiaDataRepositories<TestIdentityDbContext>();
    }
}
