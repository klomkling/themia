using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Xunit;

namespace Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests;

/// <summary>Runs the same shared data-layer contract against the EF-Core-on-PostgreSQL provider.</summary>
[Trait("Category", "Integration")]
public sealed class EfPostgresConformanceTests(PostgresContainerFixture fixture)
    : DataLayerConformanceTests, IClassFixture<PostgresContainerFixture>
{
    /// <inheritdoc />
    protected override Task<ConformanceScope> NewScopeAsync(TenantId? tenant)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        services.AddThemiaPostgres<WidgetDbContext>(configuration);
        services.AddThemiaDataRepositories<WidgetDbContext>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var filter = scope.ServiceProvider.GetRequiredService<IDataFilterScope>();

        return Task.FromResult(new ConformanceScope(provider, scope, repo, uow, filter));
    }

    /// <inheritdoc />
    protected override Task ResetAsync() => fixture.ResetAsync();
}
