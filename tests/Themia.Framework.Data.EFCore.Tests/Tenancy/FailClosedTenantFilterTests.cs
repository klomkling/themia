using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Tenancy;

/// <summary>
/// Pins the fail-closed invariant: when the current tenant id is null, the global query
/// filter MUST return only global rows (TenantId == null) and NEVER another tenant's rows.
///
/// This test exists as a regression guard: if the filter is ever changed to fail-open
/// (null current tenant → return all rows), the "DoesNotContain" assertion will catch it.
/// </summary>
public class FailClosedTenantFilterTests
{
    [Fact]
    public void Query_WithNullCurrentTenant_ReturnsOnlyGlobalRows_NeverOtherTenants()
    {
        // Arrange: null current tenant → context constructed with tenantId = null.
        using var context = CreateContext(tenantId: null);

        // Seed three rows: one global, one owned by "t1", one owned by "t2".
        var global = new TenantItem(null, "global");
        var t1Row = new TenantItem(new TenantId("t1"), "t1");
        var t2Row = new TenantItem(new TenantId("t2"), "t2");
        context.AddRange(global, t1Row, t2Row);
        context.SaveChanges();

        // Act: query through the DbSet — the global query filter applies.
        var results = context.Items.AsNoTracking().ToList();

        // Assert (a): the global row IS returned.
        Assert.Contains(results, item => item.Name == "global");

        // Assert (b) — the critical fail-closed assertion:
        // tenant-owned rows MUST NOT be returned when there is no current tenant.
        Assert.DoesNotContain(results, item => item.Name == "t1");
        Assert.DoesNotContain(results, item => item.Name == "t2");
    }

    [Fact]
    public void Query_WithNullCurrentTenant_RuntimeAccess_ReturnsOnlyGlobalRows_NeverOtherTenants()
    {
        // Arrange: share one InMemory database between the seed context and the query context.
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<RuntimeTestContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        // Seed using a real tenant context so the accessor is non-null during SaveChanges
        // (saves are not filtered, but this mirrors realistic production seeding).
        using (var seedContext = new RuntimeTestContext(options, new TenantContext(new TenantId("t1"))))
        {
            seedContext.AddRange(
                new TenantItem(null, "global"),
                new TenantItem(new TenantId("t1"), "t1"),
                new TenantItem(new TenantId("t2"), "t2"));
            seedContext.SaveChanges();
        }
        // seedContext.Dispose() has restored TenantContextAccessor to its pre-seed value.

        // Act: query with a null tenant context — the RuntimeTenantAccess filter reads the
        // accessor at query time and must see null, yielding only global rows.
        var previousTenantId = TenantContextAccessor.CurrentTenantId;
        try
        {
            TenantContextAccessor.CurrentTenantId = null;

            using var queryContext = new RuntimeTestContext(options, new TenantContext(null));
            var results = queryContext.Items.AsNoTracking().ToList();

            // Assert (a): global row IS returned.
            Assert.Contains(results, item => item.Name == "global");

            // Assert (b) — the critical fail-closed assertion:
            // tenant-owned rows MUST NOT be returned when the current tenant is null.
            Assert.DoesNotContain(results, item => item.Name == "t1");
            Assert.DoesNotContain(results, item => item.Name == "t2");
        }
        finally
        {
            // Restore AsyncLocal state to avoid leaking into other tests.
            TenantContextAccessor.CurrentTenantId = previousTenantId;
        }
    }

    private static TestContext CreateContext(TenantId? tenantId)
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestContext(options, new TenantContext(tenantId));
    }

    private sealed class TestContext : ThemiaDbContext
    {
        public TestContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        public DbSet<TenantItem> Items => Set<TenantItem>();

        protected override TenantIsolationStrategy TenantIsolationStrategy
            => TenantIsolationStrategy.PerTenantModel;
    }

    private sealed class RuntimeTestContext : ThemiaDbContext
    {
        public RuntimeTestContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        public DbSet<TenantItem> Items => Set<TenantItem>();

        protected override TenantIsolationStrategy TenantIsolationStrategy
            => TenantIsolationStrategy.RuntimeTenantAccess;
    }

    private sealed class TenantItem : ITenantEntity
    {
        // Parameterless constructor required by EF Core during materialization.
        public TenantItem()
        {
        }

        public TenantItem(TenantId? tenantId, string name)
        {
            TenantId = tenantId;
            Name = name;
        }

        public int Id { get; set; }
        public TenantId? TenantId { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
