using Microsoft.EntityFrameworkCore;
using Xunit;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Data.EFCore;

namespace Themia.Framework.Data.EFCore.Tests.Concurrency;

public class ConcurrencyAwareEntityTests
{
    [Fact]
    public void ConcurrencyAwareEntity_HasRowVersionProperty()
    {
        var product = new ConcurrencyProduct { Id = 1, Name = "Product 1" };

        // Property should exist (even if null initially)
        Assert.True(product is IConcurrencyAware);
    }

    [Fact]
    public void ThemiaDbContext_ConfiguresRowVersionAsConcurrencyToken()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateContext(dbName);

        // Verify RowVersion is configured as a concurrency token
        var entityType = context.Model.FindEntityType(typeof(ConcurrencyProduct));
        Assert.NotNull(entityType);

        var rowVersionProperty = entityType.FindProperty(nameof(IConcurrencyAware.RowVersion));
        Assert.NotNull(rowVersionProperty);
        Assert.True(rowVersionProperty.IsConcurrencyToken);
    }

    [Fact]
    public async Task Update_WithCurrentRowVersion_Succeeds()
    {
        var dbName = Guid.NewGuid().ToString();

        using (var context1 = CreateContext(dbName))
        {
            var product = new ConcurrencyProduct { Id = 1, Name = "Product 1" };
            context1.Products.Add(product);
            await context1.SaveChangesAsync();
        }

        using (var context2 = CreateContext(dbName))
        {
            var product = await context2.Products.FindAsync(1);
            Assert.NotNull(product);

            product.Name = "Updated Product";
            await context2.SaveChangesAsync();

            // Should succeed without exception
            Assert.Equal("Updated Product", product.Name);
        }
    }

    private static TestDbContext CreateContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        return new TestDbContext(options);
    }

    private sealed class TestDbContext : ThemiaDbContext
    {
        public TestDbContext(DbContextOptions options) : base(options, null)
        {
        }

        public DbSet<ConcurrencyProduct> Products => Set<ConcurrencyProduct>();
    }

    private sealed class ConcurrencyProduct : ConcurrencyAwareEntity<int>
    {
        public new int Id { get; set; } // EF Core needs public setter for materialization
        public string Name { get; set; } = string.Empty;
    }
}
