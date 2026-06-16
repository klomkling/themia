using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Xunit;

namespace Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests;

/// <summary>
/// EF-Core-specific coverage of <see cref="IRepository{T,TKey}.UpdateWhereAsync"/>'s change-tracker handling.
/// These facts cannot live in the provider-agnostic conformance base: they inspect the EF change tracker to
/// prove that only <c>Unchanged</c> entities are detached, while <c>Added</c> and <c>Modified</c> entries
/// (a caller's un-saved intent) survive the bulk write and persist on the next SaveChanges.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfUpdateWhereDetachTests(PostgresContainerFixture fixture)
    : IClassFixture<PostgresContainerFixture>
{
    private static Widget NewWidget(string name, int qty)
    {
        var w = new Widget { Name = name, Quantity = qty };
        w.SetId(Guid.NewGuid());
        return w;
    }

    [Fact]
    public async Task UpdateWhere_DetachesUnchanged_SoReReadObservesBulkWrite()
    {
        await fixture.ResetAsync();
        await using var scope = NewScope(new TenantId("acme"));

        var widget = NewWidget("tracked", 1);
        await scope.Repo.AddAsync(widget);
        await scope.Uow.SaveChangesAsync();
        // After SaveChanges the inserted row is tracked as Unchanged.
        Assert.Equal(EntityState.Unchanged, scope.Context.Entry(widget).State);

        await scope.Repo.UpdateWhereAsync(new WidgetByNameSpec("tracked"), set => set.Set(w => w.Quantity, 99));

        // The Unchanged entry was detached, so re-reading in the SAME context re-queries the DB and sees 99,
        // not the stale tracked copy still holding Quantity == 1.
        Assert.Equal(EntityState.Detached, scope.Context.Entry(widget).State);
        var reread = await scope.Repo.GetByIdAsync(widget.Id);
        Assert.NotNull(reread);
        Assert.Equal(99, reread!.Quantity);
    }

    [Fact]
    public async Task UpdateWhere_PreservesAdded_StagedInsertStillPersists()
    {
        await fixture.ResetAsync();
        await using var scope = NewScope(new TenantId("acme"));

        // An existing row to bulk-update, plus a staged-but-unsaved insert in the same scope.
        var existing = NewWidget("existing", 1);
        await scope.Repo.AddAsync(existing);
        await scope.Uow.SaveChangesAsync();

        var staged = NewWidget("staged", 5);
        await scope.Repo.AddAsync(staged);
        Assert.Equal(EntityState.Added, scope.Context.Entry(staged).State);

        await scope.Repo.UpdateWhereAsync(new WidgetByNameSpec("existing"), set => set.Set(w => w.Quantity, 42));

        // The Added entry must survive the detach loop and still flush on the later SaveChanges.
        Assert.Equal(EntityState.Added, scope.Context.Entry(staged).State);
        await scope.Uow.SaveChangesAsync();

        await using var check = NewScope(new TenantId("acme"));
        var persisted = await check.Repo.GetByIdAsync(staged.Id);
        Assert.NotNull(persisted);
        Assert.Equal(5, persisted!.Quantity);
    }

    [Fact]
    public async Task UpdateWhere_PreservesModified_PendingEditPersistsOnSaveChanges()
    {
        await fixture.ResetAsync();
        await using var scope = NewScope(new TenantId("acme"));

        var edited = NewWidget("edited", 1);
        var unrelated = NewWidget("unrelated", 1);
        await scope.Repo.AddAsync(edited);
        await scope.Repo.AddAsync(unrelated);
        await scope.Uow.SaveChangesAsync();

        // Mutate (Modified) but DON'T save, then run a bulk update for an UNRELATED predicate.
        edited.Quantity = 7;
        scope.Repo.Update(edited);
        Assert.Equal(EntityState.Modified, scope.Context.Entry(edited).State);

        await scope.Repo.UpdateWhereAsync(new WidgetByNameSpec("unrelated"), set => set.Set(w => w.Quantity, 99));

        // Fix 1: the Modified entry is NOT silently dropped — it retains the pending edit and persists it.
        Assert.Equal(EntityState.Modified, scope.Context.Entry(edited).State);
        await scope.Uow.SaveChangesAsync();

        await using var check = NewScope(new TenantId("acme"));
        var persisted = await check.Repo.GetByIdAsync(edited.Id);
        Assert.NotNull(persisted);
        Assert.Equal(7, persisted!.Quantity);
    }

    private EfScope NewScope(TenantId? tenant)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        services.AddThemiaPostgres<WidgetDbContext>(
            configuration,
            configureOptions: o => o.UseSnakeCaseNamingConvention());
        services.AddThemiaDataRepositories<WidgetDbContext>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = scope.ServiceProvider.GetRequiredService<WidgetDbContext>();
        return new EfScope(provider, scope, repo, uow, context);
    }

    private sealed record EfScope(
        IAsyncDisposable Root,
        IAsyncDisposable Scope,
        IRepository<Widget, Guid> Repo,
        IUnitOfWork Uow,
        WidgetDbContext Context) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Scope.DisposeAsync();
            await Root.DisposeAsync();
        }
    }
}
