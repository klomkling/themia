using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.Dapper.MySql.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.MySql.IntegrationTests;

/// <summary>
/// A duplicate primary key (a unique constraint) must surface as the framework's typed
/// <see cref="UniqueConstraintException"/> from the Dapper peer on MySQL. Detection here relies on the
/// default SQLSTATE interpreter matching MySQL's <c>23000</c> integrity-constraint class (error 1062).
/// </summary>
[Trait("Category", "Integration")]
public sealed class UniqueConstraintTests(MySqlContainerFixture fixture) : IClassFixture<MySqlContainerFixture>
{
    [Fact]
    public async Task Dapper_DuplicatePrimaryKey_ThrowsUniqueConstraintException()
    {
        await fixture.ResetAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaDapperMySql(configuration);
        await using var provider = services.BuildServiceProvider();

        var id = Guid.NewGuid();
        await InsertWidgetAsync(provider, id, "first");

        await Assert.ThrowsAsync<UniqueConstraintException>(() => InsertWidgetAsync(provider, id, "duplicate"));
    }

    private static async Task InsertWidgetAsync(IServiceProvider provider, Guid id, string name)
    {
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var widget = new Widget { Name = name, Quantity = 1 };
        widget.SetId(id);
        await repo.AddAsync(widget);
        await uow.SaveChangesAsync();
    }
}
