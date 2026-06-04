using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Tenancy;

/// <summary>
/// Regression tests ensuring soft-deleted tenant entities are properly filtered.
/// </summary>
public class SoftDeleteTenantFilterTests
{
    [Fact]
    public void SoftDeletedTenantEntity_IsFilteredOut()
    {
        var tenantA = new TenantId("tenant-a");
        var dbName = Guid.NewGuid().ToString();

        // Seed data
        using (var context = CreateContext(dbName, tenantA))
        {
            context.Orders.Add(new Order
            {
                Id = 1,
                Name = "Active Order",
                TenantId = tenantA,
                IsDeleted = false
            });

            context.Orders.Add(new Order
            {
                Id = 2,
                Name = "Deleted Order",
                TenantId = tenantA,
                IsDeleted = true
            });

            context.SaveChanges();
        }

        // Verify soft-deleted entity is filtered
        using (var context = CreateContext(dbName, tenantA))
        {
            var orders = context.Orders.ToList();

            Assert.Single(orders);
            Assert.Equal("Active Order", orders[0].Name);
            Assert.DoesNotContain(orders, o => o.IsDeleted);
        }
    }

    [Fact]
    public void SoftDeletedEntity_InOtherTenant_IsNotAccessible()
    {
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        var dbName = Guid.NewGuid().ToString();

        // Seed data for both tenants
        using (var context = CreateContext(dbName, null))
        {
            context.Orders.Add(new Order { Id = 1, Name = "Tenant A Active", TenantId = tenantA, IsDeleted = false });
            context.Orders.Add(new Order { Id = 2, Name = "Tenant A Deleted", TenantId = tenantA, IsDeleted = true });
            context.Orders.Add(new Order { Id = 3, Name = "Tenant B Active", TenantId = tenantB, IsDeleted = false });
            context.Orders.Add(new Order { Id = 4, Name = "Tenant B Deleted", TenantId = tenantB, IsDeleted = true });
            context.SaveChanges();
        }

        // Tenant A should only see their own active records
        using (var context = CreateContext(dbName, tenantA))
        {
            var orders = context.Orders.ToList();

            Assert.Single(orders);
            Assert.Equal("Tenant A Active", orders[0].Name);
            Assert.Equal(tenantA, orders[0].TenantId);
            Assert.False(orders[0].IsDeleted);
        }

        // Tenant B should only see their own active records
        using (var context = CreateContext(dbName, tenantB))
        {
            var orders = context.Orders.ToList();

            Assert.Single(orders);
            Assert.Equal("Tenant B Active", orders[0].Name);
            Assert.Equal(tenantB, orders[0].TenantId);
            Assert.False(orders[0].IsDeleted);
        }
    }

    [Fact]
    public void SoftDeleteFilterCombinesWithTenantFilter()
    {
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        var dbName = Guid.NewGuid().ToString();

        // Seed various combinations
        using (var context = CreateContext(dbName, null))
        {
            context.Orders.Add(new Order { Id = 1, Name = "A-Active", TenantId = tenantA, IsDeleted = false });
            context.Orders.Add(new Order { Id = 2, Name = "A-Deleted", TenantId = tenantA, IsDeleted = true });
            context.Orders.Add(new Order { Id = 3, Name = "B-Active", TenantId = tenantB, IsDeleted = false });
            context.Orders.Add(new Order { Id = 4, Name = "B-Deleted", TenantId = tenantB, IsDeleted = true });
            context.SaveChanges();
        }

        // Tenant A should only see their active records
        using (var context = CreateContext(dbName, tenantA))
        {
            var orders = context.Orders.ToList();

            Assert.Single(orders);
            Assert.Equal("A-Active", orders[0].Name);
            Assert.Equal(tenantA, orders[0].TenantId);
            Assert.False(orders[0].IsDeleted);
        }

        // Tenant B should only see their active records
        using (var context = CreateContext(dbName, tenantB))
        {
            var orders = context.Orders.ToList();

            Assert.Single(orders);
            Assert.Equal("B-Active", orders[0].Name);
            Assert.Equal(tenantB, orders[0].TenantId);
            Assert.False(orders[0].IsDeleted);
        }

        // With IgnoreQueryFilters, see all records
        using (var context = CreateContext(dbName, tenantA))
        {
            var orders = context.Orders.IgnoreQueryFilters().ToList();

            Assert.Equal(4, orders.Count);
        }
    }

    [Fact]
    public void Find_BlocksAccessTo_OtherTenantEntity_EvenIfNotDeleted()
    {
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        var dbName = Guid.NewGuid().ToString();

        // Seed entity for tenant B
        using (var context = CreateContext(dbName, null))
        {
            context.Orders.Add(new Order
            {
                Id = 1,
                Name = "Tenant B Order",
                TenantId = tenantB,
                IsDeleted = false
            });
            context.SaveChanges();
        }

        // Tenant A tries to Find tenant B's entity
        using (var context = CreateContext(dbName, tenantA))
        {
            var order = context.Orders.Find(1);

            // Find now validates tenant access - should return null for other tenant
            Assert.Null(order);
        }
    }

    [Fact]
    public void IgnoreQueryFilters_ShowsSoftDeletedRecords()
    {
        var tenantA = new TenantId("tenant-a");
        var dbName = Guid.NewGuid().ToString();

        using (var context = CreateContext(dbName, tenantA))
        {
            context.Orders.Add(new Order { Id = 1, Name = "Active", TenantId = tenantA, IsDeleted = false });
            context.Orders.Add(new Order { Id = 2, Name = "Deleted", TenantId = tenantA, IsDeleted = true });
            context.SaveChanges();
        }

        // With filters
        using (var context = CreateContext(dbName, tenantA))
        {
            var orders = context.Orders.ToList();
            Assert.Single(orders);
        }

        // Without filters - should see both
        using (var context = CreateContext(dbName, tenantA))
        {
            var orders = context.Orders.IgnoreQueryFilters().ToList();
            Assert.Equal(2, orders.Count);
        }
    }

    [Fact]
    public void Find_SoftDeletedEntity_ReturnsNull()
    {
        var tenantA = new TenantId("tenant-a");
        var dbName = Guid.NewGuid().ToString();

        using (var context = CreateContext(dbName, tenantA))
        {
            context.Orders.Add(new Order { Id = 1, Name = "Deleted", TenantId = tenantA, IsDeleted = true });
            context.SaveChanges();
        }

        using (var context = CreateContext(dbName, tenantA))
        {
            var order = context.Orders.Find(1);
            Assert.Null(order);
        }
    }

    [Fact]
    public void GlobalSoftDeletedRecords_AreFilteredForAllTenants()
    {
        var tenantA = new TenantId("tenant-a");
        var dbName = Guid.NewGuid().ToString();

        using (var context = CreateContext(dbName, null))
        {
            context.Orders.Add(new Order { Id = 1, Name = "Global Active", TenantId = null, IsDeleted = false });
            context.Orders.Add(new Order { Id = 2, Name = "Global Deleted", TenantId = null, IsDeleted = true });
            context.SaveChanges();
        }

        // Tenant A should see active global record but not deleted one
        using (var context = CreateContext(dbName, tenantA))
        {
            var orders = context.Orders.ToList();

            Assert.Single(orders);
            Assert.Equal("Global Active", orders[0].Name);
            Assert.Null(orders[0].TenantId);
        }
    }

    private static TestDbContext CreateContext(string dbName, TenantId? tenantId)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var tenantContext = tenantId != null ? new TenantContext(tenantId) : null;

        return new TestDbContext(options, tenantContext);
    }

    private sealed class TestDbContext : ThemiaDbContext
    {
        public TestDbContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        public DbSet<Order> Orders => Set<Order>();

        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.PerTenantModel;
    }

    private sealed class Order : ITenantEntity, ISoftDeletable
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public TenantId? TenantId { get; set; }
        public bool IsDeleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? RestoredAt { get; set; }
        public string? RestoredBy { get; set; }
    }
}
