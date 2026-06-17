using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.SqlServer;
using Themia.Modules.Storage.IntegrationTests;
using Themia.Modules.Storage.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Storage.EFCore.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class EfSqlServerStorageTests(SqlServerStorageFixture fixture)
    : StorageConformanceTests, IClassFixture<SqlServerStorageFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        services.AddThemiaSqlServer<TestStorageDbContext>(configuration);
        services.AddThemiaDataRepositories<TestStorageDbContext>();
    }
}
