using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Security;

/// <summary>
/// CRITICAL SECURITY TESTS: Verifies that EF Core model caching does not cause
/// tenant filter to be "frozen" to the first context instance.
///
/// Bug scenario: If query filters use Expression.Constant(this), the first
/// DbContext instance gets baked into the cached model, causing all subsequent
/// contexts to filter by the wrong tenant.
///
/// These tests ensure the fix (using static TenantContextAccessor) works correctly.
/// </summary>
public class ModelCacheTenantIsolationTests
{
    [Fact]
    public void MultipleContexts_WithDifferentTenants_EachSeesOwnData()
    {
        // This is the core test for the bug fix
        // Without the fix, both contexts would see tenant A's data (the first context)
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        // Seed data for both tenants (no tenant context during seeding)
        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Product A", TenantId = tenantA });
            seedContext.Products.Add(new Product { Id = 2, Name = "Product B", TenantId = tenantB });
            seedContext.SaveChanges();
        }

        // CRITICAL: Create context A first (this builds the model cache)
        using (var contextA = CreateContext(dbName, tenantA))
        {
            var productsA = contextA.Products.ToList();

            Assert.Single(productsA);
            Assert.Equal("Product A", productsA[0].Name);
            Assert.Equal(tenantA, productsA[0].TenantId);
        }

        // CRITICAL: Create context B second (reuses cached model)
        // BUG: If Expression.Constant(this) was used, this context would filter by tenant A!
        // FIX: With TenantContextAccessor, this context correctly filters by tenant B
        using (var contextB = CreateContext(dbName, tenantB))
        {
            var productsB = contextB.Products.ToList();

            Assert.Single(productsB);
            Assert.Equal("Product B", productsB[0].Name);
            Assert.Equal(tenantB, productsB[0].TenantId);

            // CRITICAL: Ensure tenant B does NOT see tenant A's data
            Assert.DoesNotContain(productsB, p => p.TenantId == tenantA);
        }
    }

    [Fact]
    public void MultipleContexts_InterleavedCreation_CorrectIsolation()
    {
        // Tests that contexts can be created in any order without cross-contamination
        var dbName = Guid.NewGuid().ToString();
        var tenant1 = new TenantId("tenant-1");
        var tenant2 = new TenantId("tenant-2");
        var tenant3 = new TenantId("tenant-3");

        // Seed
        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "P1", TenantId = tenant1 });
            seedContext.Products.Add(new Product { Id = 2, Name = "P2", TenantId = tenant2 });
            seedContext.Products.Add(new Product { Id = 3, Name = "P3", TenantId = tenant3 });
            seedContext.SaveChanges();
        }

        // Create multiple contexts in interleaved fashion
        var context1 = CreateContext(dbName, tenant1);
        var context2 = CreateContext(dbName, tenant2);
        var context3 = CreateContext(dbName, tenant3);

        try
        {
            // Query from context2 first (not the first created)
            var products2 = context2.Products.ToList();
            Assert.Single(products2);
            Assert.Equal("P2", products2[0].Name);

            // Query from context1 (first created)
            var products1 = context1.Products.ToList();
            Assert.Single(products1);
            Assert.Equal("P1", products1[0].Name);

            // Query from context3 (last created)
            var products3 = context3.Products.ToList();
            Assert.Single(products3);
            Assert.Equal("P3", products3[0].Name);

            // Re-query to ensure filters are stable
            var products2Again = context2.Products.ToList();
            Assert.Single(products2Again);
            Assert.Equal("P2", products2Again[0].Name);
        }
        finally
        {
            context1.Dispose();
            context2.Dispose();
            context3.Dispose();
        }
    }

    [Fact]
    public void ContextRecreation_WithSameTenant_SeesConsistentData()
    {
        // Verifies that recreating a context with the same tenant works correctly
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Product A", TenantId = tenantA });
            seedContext.Products.Add(new Product { Id = 2, Name = "Product B", TenantId = tenantB });
            seedContext.SaveChanges();
        }

        // Create, query, dispose
        using (var context1 = CreateContext(dbName, tenantA))
        {
            Assert.Single(context1.Products.ToList());
        }

        // Recreate with same tenant - should work correctly
        using (var context2 = CreateContext(dbName, tenantA))
        {
            var products = context2.Products.ToList();
            Assert.Single(products);
            Assert.Equal("Product A", products[0].Name);
        }

        // Recreate with different tenant - should see different data
        using (var context3 = CreateContext(dbName, tenantB))
        {
            var products = context3.Products.ToList();
            Assert.Single(products);
            Assert.Equal("Product B", products[0].Name);
        }
    }

    [Fact]
    public void NoTenantContext_OnlySeesGlobalRecords()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");

        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Global", TenantId = null });
            seedContext.Products.Add(new Product { Id = 2, Name = "Tenant A", TenantId = tenantA });
            seedContext.SaveChanges();
        }

        // Context with no tenant should only see global records
        using (var contextNoTenant = CreateContext(dbName, null))
        {
            var products = contextNoTenant.Products.ToList();

            Assert.Single(products);
            Assert.Equal("Global", products[0].Name);
            Assert.Null(products[0].TenantId);
        }

        // Context with tenant should see global + tenant records
        using (var contextWithTenant = CreateContext(dbName, tenantA))
        {
            var products = contextWithTenant.Products.ToList();

            Assert.Equal(2, products.Count);
            Assert.Contains(products, p => p.Name == "Global");
            Assert.Contains(products, p => p.Name == "Tenant A");
        }
    }

    [Fact]
    public async Task AsyncContextAccess_MaintainsTenantIsolation()
    {
        // Verifies tenant isolation with async/await (AsyncLocal should work correctly)
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        using (var seedContext = CreateContext(dbName, null))
        {
            seedContext.Products.Add(new Product { Id = 1, Name = "Product A", TenantId = tenantA });
            seedContext.Products.Add(new Product { Id = 2, Name = "Product B", TenantId = tenantB });
            seedContext.SaveChanges();
        }

        // Simulate async operations with different tenants
        var task1 = QueryTenantDataAsync(dbName, tenantA, "Product A");
        var task2 = QueryTenantDataAsync(dbName, tenantB, "Product B");

        await Task.WhenAll(task1, task2);

        // Both tasks should have completed successfully with correct data
        Assert.True(task1.IsCompletedSuccessfully);
        Assert.True(task2.IsCompletedSuccessfully);
    }

    [Fact]
    public void DifferentTenants_HaveDifferentModels()
    {
        // Verify that each tenant gets its own compiled model
        // This is the fix for the tenant filter "freezing" bug
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        using var contextA = CreateContext(dbName, tenantA);
        using var contextB = CreateContext(dbName, tenantB);

        // Different tenants should have different models
        Assert.NotSame(contextA.Model, contextB.Model);
    }

    private static async Task QueryTenantDataAsync(string dbName, TenantId tenantId, string expectedName)
    {
        await Task.Delay(10); // Simulate async work

        using var context = CreateContext(dbName, tenantId);
        var products = await context.Products.ToListAsync();

        Assert.Single(products);
        Assert.Equal(expectedName, products[0].Name);
        Assert.Equal(tenantId, products[0].TenantId);
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

        // These tests verify PerTenantModel strategy (original fix for the bug)
        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.PerTenantModel;
    }

    private sealed class Product : ITenantEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public TenantId? TenantId { get; set; }
    }
}
