using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.EFCore.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Identity.AspNetCore.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.IntegrationTests;

/// <summary>External-login HTTP conformance: EF Core + PostgreSQL peer.</summary>
[Trait("Category", "Integration")]
public sealed class EfPostgresExternalAuthTests(PostgresAuthFixture fixture)
    : ExternalAuthConformanceTests, IClassFixture<PostgresAuthFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;

    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void RegisterIdentity(IServiceCollection services, Action<IdentityModuleOptions> configure)
        => services.AddThemiaIdentityEFCore(configure);

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaPostgres<TestIdentityDbContext>(configuration);
        services.AddThemiaDataRepositories<TestIdentityDbContext>();
    }
}
