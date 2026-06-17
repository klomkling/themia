using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Storage.IntegrationTests;
using Themia.Modules.Storage.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Storage.EFCore.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class EfPostgresStorageTests(PostgresStorageFixture fixture)
    : StorageConformanceTests, IClassFixture<PostgresStorageFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaPostgres<TestStorageDbContext>(configuration);
        services.AddThemiaDataRepositories<TestStorageDbContext>();
    }
}
