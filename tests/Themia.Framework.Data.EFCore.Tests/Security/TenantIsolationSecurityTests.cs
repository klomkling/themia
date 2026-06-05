using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Security;

/// <summary>
/// Security tests verifying tenant isolation cannot be bypassed accidentally.
/// </summary>
public class TenantIsolationSecurityTests
{
    [Fact]
    public void CannotAccessOtherTenantData_ThroughStandardQuery()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        // Seed data for both tenants
        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Product A", TenantId = tenantA });
            seedContext.Products.Add(new Product { Id = 2, Name = "Product B", TenantId = tenantB });
            seedContext.SaveChanges();
        }

        // Verify tenant A cannot see tenant B's data
        using (var contextA = CreateContext(dbName, tenantA))
        {
            var products = contextA.Products.ToList();

            Assert.Single(products);
            Assert.Equal("Product A", products[0].Name);
            Assert.DoesNotContain(products, p => p.TenantId == tenantB);
        }

        // Verify tenant B cannot see tenant A's data
        using (var contextB = CreateContext(dbName, tenantB))
        {
            var products = contextB.Products.ToList();

            Assert.Single(products);
            Assert.Equal("Product B", products[0].Name);
            Assert.DoesNotContain(products, p => p.TenantId == tenantA);
        }
    }

    [Fact]
    public void CannotAccessOtherTenantData_ThroughFind()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Product A", TenantId = tenantA });
            seedContext.Products.Add(new Product { Id = 2, Name = "Product B", TenantId = tenantB });
            seedContext.SaveChanges();
        }

        // Tenant A tries to access product 2 (owned by tenant B) via Find
        using (var contextA = CreateContext(dbName, tenantA))
        {
            var product = contextA.Products.Find(2);

            // Find should be protected by tenant validation
            Assert.Null(product);
        }
    }

    [Fact]
    public void CannotAccessOtherTenantData_ThroughFirstOrDefault()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Product A", TenantId = tenantA });
            seedContext.Products.Add(new Product { Id = 2, Name = "Product B", TenantId = tenantB });
            seedContext.SaveChanges();
        }

        // Tenant A tries to access tenant B's product
        using (var contextA = CreateContext(dbName, tenantA))
        {
            var product = contextA.Products
                .FirstOrDefault(p => p.Id == 2);

            Assert.Null(product);
        }
    }

    [Fact]
    public void IgnoreQueryFilters_AllowsCrossTenantAccess_WhenExplicit()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Product A", TenantId = tenantA });
            seedContext.Products.Add(new Product { Id = 2, Name = "Product B", TenantId = tenantB });
            seedContext.SaveChanges();
        }

        // Admin/superuser scenario: explicitly bypass filters
        using (var contextA = CreateContext(dbName, tenantA))
        {
            var allProducts = contextA.Products
                .IgnoreQueryFilters()
                .ToList();

            Assert.Equal(2, allProducts.Count);
        }
    }

    [Fact]
    public void GlobalRecords_AreAccessibleByAllTenants()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Global Product", TenantId = null });
            seedContext.Products.Add(new Product { Id = 2, Name = "Tenant A Product", TenantId = tenantA });
            seedContext.SaveChanges();
        }

        // Tenant A sees global + own
        using (var contextA = CreateContext(dbName, tenantA))
        {
            var products = contextA.Products.ToList();
            Assert.Equal(2, products.Count);
            Assert.Contains(products, p => p.Name == "Global Product");
            Assert.Contains(products, p => p.Name == "Tenant A Product");
        }

        // Tenant B sees only global
        using (var contextB = CreateContext(dbName, tenantB))
        {
            var products = contextB.Products.ToList();
            Assert.Single(products);
            Assert.Equal("Global Product", products[0].Name);
        }
    }

    [Fact]
    public void TenantContext_CannotBeSpoofed_ViaQueryParameters()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Product A", TenantId = tenantA });
            seedContext.Products.Add(new Product { Id = 2, Name = "Product B", TenantId = tenantB });
            seedContext.SaveChanges();
        }

        // Even if query includes TenantId, the context filter still applies
        using (var contextA = CreateContext(dbName, tenantA))
        {
            var products = contextA.Products
                .Where(p => p.TenantId == tenantB) // Try to access tenant B
                .ToList();

            // Should get no results due to query filter
            Assert.Empty(products);
        }
    }

    private static TestDbContext CreateContext(string dbName, TenantId? tenantId)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var tenantContext = tenantId != null
            ? new TenantContext(tenantId, "test")
            : null;

        return new TestDbContext(options, tenantContext);
    }

    private sealed class TestDbContext : ThemiaDbContext
    {
        public TestDbContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        public DbSet<Product> Products => Set<Product>();

        // These tests verify tenant isolation works with both strategies
        // Using PerTenantModel for consistency with original security tests
        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.PerTenantModel;
    }

    private sealed class Product : ITenantEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public TenantId? TenantId { get; set; }
    }
}
