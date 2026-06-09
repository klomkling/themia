using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.Dapper.PostgreSql.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests;

/// <summary>Runs the shared data-layer contract against the Dapper-on-PostgreSQL provider.</summary>
[Trait("Category", "Integration")]
public sealed class DapperPostgresConformanceTests(PostgresContainerFixture fixture)
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
        services.AddThemiaDapperPostgres(configuration);

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
    /// A no-tenant (system) scope must not be able to mutate a tenant-owned row by primary key: Dapper writes
    /// are scoped to global (tenant_id IS NULL) records when no tenant is ambient, mirroring the read path.
    /// </summary>
    [Fact]
    public async Task NoTenantScope_CannotSoftDelete_TenantOwnedRow()
    {
        await ResetAsync();

        Guid id;
        await using (var a = await NewScopeAsync(new TenantId("a")))
        {
            var w = new Widget { Name = "owned", Quantity = 1 };
            w.SetId(Guid.NewGuid());
            id = w.Id;
            await a.Repo.AddAsync(w);
            await a.Uow.SaveChangesAsync();
        }

        await using (var system = await NewScopeAsync(null))
        {
            var detached = new Widget { Name = "owned", Quantity = 1 };
            detached.SetId(id);
            system.Repo.Remove(detached);
            // WHERE id = @id AND tenant_id IS NULL matches 0 rows, so the cross-tenant delete fails loud.
            await Assert.ThrowsAsync<ConcurrencyException>(() => system.Uow.SaveChangesAsync());
        }

        await using var check = await NewScopeAsync(new TenantId("a"));
        Assert.NotNull(await check.Repo.GetByIdAsync(id));   // the tenant row survived the cross-tenant delete attempt
    }

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
        services.AddThemiaDapperPostgres(configuration);
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
