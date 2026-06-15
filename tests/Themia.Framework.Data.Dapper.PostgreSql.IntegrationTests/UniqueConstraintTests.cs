using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.Dapper.PostgreSql.DependencyInjection;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Xunit;

namespace Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests;

/// <summary>
/// A duplicate primary key (a unique constraint) must surface as the framework's typed
/// <see cref="UniqueConstraintException"/> from both data peers on PostgreSQL. This is the
/// "insert-with-unique-key + catch" compare-and-set primitive concurrency-safe rotation relies on.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UniqueConstraintTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task Dapper_DuplicatePrimaryKey_ThrowsUniqueConstraintException()
    {
        await fixture.ResetAsync();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaDapperPostgres(Configuration());
        await using var provider = services.BuildServiceProvider();

        var id = Guid.NewGuid();
        await InsertWidgetAsync(provider, id, "first");

        await Assert.ThrowsAsync<UniqueConstraintException>(() => InsertWidgetAsync(provider, id, "duplicate"));
    }

    [Fact]
    public async Task EfCore_DuplicatePrimaryKey_ThrowsUniqueConstraintException()
    {
        await fixture.ResetAsync();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaPostgres<WidgetDbContext>(Configuration(), configureOptions: o => o.UseSnakeCaseNamingConvention());
        services.AddThemiaDataRepositories<WidgetDbContext>();
        await using var provider = services.BuildServiceProvider();

        var id = Guid.NewGuid();
        await InsertWidgetAsync(provider, id, "first");

        await Assert.ThrowsAsync<UniqueConstraintException>(() => InsertWidgetAsync(provider, id, "duplicate"));
    }

    private IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString })
        .Build();

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
