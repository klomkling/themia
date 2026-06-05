using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Tenancy;

public class ThemiaDbContextTenantFilterTests
{
    [Fact]
    public void TenantContextWithValue_ReturnsCurrentTenantAndGlobalRecords()
    {
        var tenantId = new TenantId("tenant-a");

        using var context = CreateContext(tenantId);

        Seed(context);

        var results = context.Items.AsNoTracking().ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.Name == "tenant-a");
        Assert.Contains(results, item => item.Name == "global");
        Assert.DoesNotContain(results, item => item.Name == "tenant-b");
    }

    [Fact]
    public void TenantContextWithoutValue_ReturnsOnlyGlobalRecords()
    {
        using var context = CreateContext(null);

        Seed(context);

        var results = context.Items.AsNoTracking().ToList();

        Assert.Single(results);
        Assert.Equal("global", results[0].Name);
    }

    [Fact]
    public void TenantContextWithValue_ExcludesGlobalRecords_WhenConfigured()
    {
        var tenantId = new TenantId("tenant-a");

        using var context = CreateContextWithoutGlobalRecords(tenantId);

        Seed(context);

        var results = context.Items.AsNoTracking().ToList();

        Assert.Single(results);
        Assert.Equal("tenant-a", results[0].Name);
    }

    [Fact]
    public void IgnoreQueryFilters_ReturnsAllRecords()
    {
        var tenantId = new TenantId("tenant-a");

        using var context = CreateContext(tenantId);

        Seed(context);

        var results = context.Items.IgnoreQueryFilters().AsNoTracking().ToList();

        Assert.Equal(3, results.Count);
    }

    private static TestThemiaDbContext CreateContext(TenantId? tenantId)
    {
        var options = new DbContextOptionsBuilder<TestThemiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantContext = new TenantContext(tenantId);

        return new TestThemiaDbContext(options, tenantContext);
    }

    private static TestThemiaDbContextWithoutGlobalRecords CreateContextWithoutGlobalRecords(TenantId? tenantId)
    {
        var options = new DbContextOptionsBuilder<TestThemiaDbContextWithoutGlobalRecords>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantContext = new TenantContext(tenantId);

        return new TestThemiaDbContextWithoutGlobalRecords(options, tenantContext);
    }

    private static void Seed(TestThemiaDbContext context)
    {
        var tenantA = new TenantItem(new TenantId("tenant-a"), "tenant-a");
        var tenantB = new TenantItem(new TenantId("tenant-b"), "tenant-b");
        var global = new TenantItem(null, "global");

        context.AddRange(tenantA, tenantB, global);
        context.SaveChanges();
    }

    private static void Seed(TestThemiaDbContextWithoutGlobalRecords context)
    {
        var tenantA = new TenantItem(new TenantId("tenant-a"), "tenant-a");
        var tenantB = new TenantItem(new TenantId("tenant-b"), "tenant-b");
        var global = new TenantItem(null, "global");

        context.AddRange(tenantA, tenantB, global);
        context.SaveChanges();
    }

    private sealed class TestThemiaDbContext : ThemiaDbContext
    {
        public TestThemiaDbContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        public DbSet<TenantItem> Items => Set<TenantItem>();

        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.PerTenantModel;
    }

    private sealed class TestThemiaDbContextWithoutGlobalRecords : ThemiaDbContext
    {
        public TestThemiaDbContextWithoutGlobalRecords(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        public DbSet<TenantItem> Items => Set<TenantItem>();

        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.PerTenantModel;

        protected override bool IncludeGlobalRecordsForTenants => false;
    }

    private sealed class TenantItem : ITenantEntity
    {
        // Parameterless constructor used by EF Core during materialization
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
