using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Specifications;
using Xunit;

namespace Themia.Framework.Data.Dapper.Conformance;

/// <summary>
/// Provider-agnostic behavioural contract for the Themia data layer. Concrete subclasses bind
/// <see cref="NewScopeAsync"/>/<see cref="ResetAsync"/> to a specific provider (Dapper or EF Core)
/// running against the same physical schema, so every fact runs identically on both.
/// </summary>
public abstract class DataLayerConformanceTests
{
    /// <summary>Builds a fresh DI scope wired to the provider under test, with the given ambient tenant.</summary>
    protected abstract Task<ConformanceScope> NewScopeAsync(TenantId? tenant);

    /// <summary>Truncates the shared table between tests so each fact starts clean.</summary>
    protected abstract Task ResetAsync();

    private static Widget NewWidget(string name, int qty)
    {
        var w = new Widget { Name = name, Quantity = qty };
        w.SetId(Guid.NewGuid());
        return w;
    }

    [Fact]
    public async Task Add_Then_GetById_RoundTrips_AndStampsAudit()
    {
        await ResetAsync();
        await using var s = await NewScopeAsync(new TenantId("acme"));

        var widget = NewWidget("alpha", 7);
        await s.Repo.AddAsync(widget);
        await s.Uow.SaveChangesAsync();

        var loaded = await s.Repo.GetByIdAsync(widget.Id);

        Assert.NotNull(loaded);
        Assert.Equal("alpha", loaded!.Name);
        Assert.True(loaded.CreatedAt > DateTimeOffset.MinValue, "CreatedAt should be audit-stamped on insert");
        Assert.Equal(new TenantId("acme"), loaded.TenantId);
    }

    [Fact]
    public async Task Tenant_A_Cannot_See_Tenant_B_Rows()
    {
        await ResetAsync();

        await using (var a = await NewScopeAsync(new TenantId("a")))
        {
            await a.Repo.AddAsync(NewWidget("a-only", 1));
            await a.Uow.SaveChangesAsync();
        }

        await using var b = await NewScopeAsync(new TenantId("b"));
        var visible = await b.Repo.ListAsync(new WidgetByNameSpec("a-only"));

        Assert.Empty(visible);
    }

    [Fact]
    public async Task Remove_SoftDeletes_And_HidesFromQueries()
    {
        await ResetAsync();
        await using var s = await NewScopeAsync(new TenantId("acme"));

        var widget = NewWidget("to-delete", 3);
        await s.Repo.AddAsync(widget);
        await s.Uow.SaveChangesAsync();

        s.Repo.Remove(widget);
        await s.Uow.SaveChangesAsync();

        var loaded = await s.Repo.GetByIdAsync(widget.Id);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task BypassTenantFilter_RevealsOtherTenants()
    {
        await ResetAsync();

        await using (var a = await NewScopeAsync(new TenantId("a")))
        {
            await a.Repo.AddAsync(NewWidget("shared", 9));
            await a.Uow.SaveChangesAsync();
        }

        await using var b = await NewScopeAsync(new TenantId("b"));

        using (b.Filter.BypassTenantFilter())
        {
            var visible = await b.Repo.ListAsync(new WidgetByNameSpec("shared"));
            Assert.Single(visible);
        }
    }

    [Fact]
    public async Task Page_ReturnsItemsAndTotal()
    {
        await ResetAsync();
        await using var s = await NewScopeAsync(new TenantId("acme"));

        // Insert in reverse order to prove the OrderBy term drives the result, not insert order.
        for (var i = 4; i >= 0; i--)
        {
            await s.Repo.AddAsync(NewWidget($"w{i}", i));
        }

        await s.Uow.SaveChangesAsync();

        var page = await s.Repo.PageAsync(new AllOrderedByNamePagedSpec(0, 2));

        Assert.Equal(5, page.Total);
        Assert.Equal(2, page.Items.Count);
        // Ordered-identity: ascending by name, first page must be "w0" then "w1".
        Assert.Equal("w0", page.Items[0].Name);
        Assert.Equal("w1", page.Items[1].Name);
    }

    [Fact]
    public async Task Transaction_Rollback_DiscardsWrites()
    {
        await ResetAsync();
        await using var s = await NewScopeAsync(new TenantId("acme"));

        await using (var tx = await s.Uow.BeginTransactionAsync())
        {
            await s.Repo.AddAsync(NewWidget("ghost", 1));
            await s.Uow.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        var visible = await s.Repo.ListAsync(new WidgetByNameSpec("ghost"));

        Assert.Empty(visible);
    }

    [Fact]
    public async Task Update_ModifiesRow_AndStampsLastModified()
    {
        await ResetAsync();
        await using var s = await NewScopeAsync(new TenantId("acme"));

        var widget = NewWidget("u", 1);
        await s.Repo.AddAsync(widget);
        await s.Uow.SaveChangesAsync();

        widget.Quantity = 42;
        s.Repo.Update(widget);
        await s.Uow.SaveChangesAsync();

        var loaded = await s.Repo.GetByIdAsync(widget.Id);

        Assert.NotNull(loaded);
        Assert.Equal(42, loaded!.Quantity);
        Assert.NotNull(loaded.LastModifiedAt);
    }

    [Fact]
    public async Task CollectionContains_List_Filters()
    {
        await ResetAsync();
        await using var s = await NewScopeAsync(new TenantId("acme"));

        for (var i = 0; i < 3; i++)
        {
            await s.Repo.AddAsync(NewWidget($"w{i}", i));
        }

        await s.Uow.SaveChangesAsync();

        var names = new List<string> { "w0", "w2" };
        var matched = await s.Repo.ListAsync(new NameInListSpec(names));

        Assert.Equal(2, matched.Count);
        Assert.Contains(matched, w => w.Name == "w0");
        Assert.Contains(matched, w => w.Name == "w2");
        Assert.DoesNotContain(matched, w => w.Name == "w1");
    }

    /// <summary>All widgets ordered by name with a single page applied (Skip/Take).</summary>
    private sealed class AllOrderedByNamePagedSpec : Specification<Widget>
    {
        public AllOrderedByNamePagedSpec(int skip, int take)
        {
            OrderBy(w => w.Name);
            Page(skip, take);
        }
    }

    /// <summary>Widgets whose name is in the supplied list (translates to <c>WHERE name IN (...)</c>).</summary>
    private sealed class NameInListSpec : Specification<Widget>
    {
        public NameInListSpec(List<string> names) => Where(w => names.Contains(w.Name));
    }
}
