using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.Dapper.MySql.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.MySql.IntegrationTests;

/// <summary>Runs the shared data-layer contract against the Dapper-on-MySQL provider.</summary>
[Trait("Category", "Integration")]
public sealed class DapperMySqlConformanceTests(MySqlContainerFixture fixture)
    : DataLayerConformanceTests, IClassFixture<MySqlContainerFixture>
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
        services.AddThemiaDapperMySql(configuration);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var filter = scope.ServiceProvider.GetRequiredService<IDataFilterScope>();

        return Task.FromResult(new ConformanceScope(provider, scope, repo, uow, filter));
    }

    /// <inheritdoc />
    protected override Task ResetAsync() => fixture.ResetAsync();

    /// <summary>
    /// Audit user columns (CreatedBy on insert, LastModifiedBy on update) are stamped from the ambient
    /// <see cref="ICurrentUserAccessor"/> by the Dapper unit of work.
    /// </summary>
    [Fact]
    public async Task AuditUser_IsStamped_OnInsertAndUpdate()
    {
        await ResetAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaDapperMySql(configuration);
        services.AddSingleton<ICurrentUserAccessor>(new StubCurrentUser("user-42"));   // override the null default
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var widget = new Widget { Name = "audited", Quantity = 1 };
        widget.SetId(Guid.NewGuid());
        await repo.AddAsync(widget);
        await uow.SaveChangesAsync();
        Assert.Equal("user-42", (await repo.GetByIdAsync(widget.Id))!.CreatedBy);

        widget.Quantity = 2;
        repo.Update(widget);
        await uow.SaveChangesAsync();
        Assert.Equal("user-42", (await repo.GetByIdAsync(widget.Id))!.LastModifiedBy);
    }

    private sealed class StubCurrentUser(string? userId) : ICurrentUserAccessor
    {
        public string? UserId { get; } = userId;
    }
}
