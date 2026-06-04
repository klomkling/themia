using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Themia.Framework.Core.Abstractions.Entities;
using Xunit;

namespace Themia.Framework.Data.EFCore.IntegrationTests.Concurrency;

/// <summary>
/// Integration tests for optimistic concurrency control with real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public class ConcurrencyIntegrationTests : IClassFixture<ConcurrencyIntegrationTests.PostgresFixture>
{
    private readonly PostgresFixture fixture;

    public ConcurrencyIntegrationTests(PostgresFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task ConcurrentUpdate_ThrowsConcurrencyException()
    {
        await fixture.ResetDataAsync();

        int inventoryId;

        // Create initial inventory
        await using (var context = fixture.CreateContext())
        {
            var inventory = new InventoryItem { ProductName = "Widget", Quantity = 100 };
            context.Inventory.Add(inventory);
            await context.SaveChangesAsync();
            inventoryId = inventory.Id;
        }

        // Simulate two concurrent updates
        await using var context1 = fixture.CreateContext();
        await using var context2 = fixture.CreateContext();

        var item1 = await context1.Inventory.FindAsync(inventoryId);
        var item2 = await context2.Inventory.FindAsync(inventoryId);

        Assert.NotNull(item1);
        Assert.NotNull(item2);

        // First update succeeds
        item1.Quantity = 90;
        await context1.SaveChangesAsync();

        // Second update should fail due to concurrency
        item2.Quantity = 95;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await context2.SaveChangesAsync());
    }

    [Fact]
    public async Task ConcurrencyConflict_CanBeResolvedWithReload()
    {
        await fixture.ResetDataAsync();

        int inventoryId;

        await using (var context = fixture.CreateContext())
        {
            var inventory = new InventoryItem { ProductName = "Widget", Quantity = 100 };
            context.Inventory.Add(inventory);
            await context.SaveChangesAsync();
            inventoryId = inventory.Id;
        }

        await using var context1 = fixture.CreateContext();
        await using var context2 = fixture.CreateContext();

        var item1 = await context1.Inventory.FindAsync(inventoryId);
        var item2 = await context2.Inventory.FindAsync(inventoryId);

        Assert.NotNull(item1);
        Assert.NotNull(item2);

        item1.Quantity = 90;
        await context1.SaveChangesAsync();

        item2.Quantity = 95;

        try
        {
            await context2.SaveChangesAsync();
            Assert.Fail("Expected DbUpdateConcurrencyException");
        }
        catch (DbUpdateConcurrencyException)
        {
            // Reload and retry
            await context2.Entry(item2).ReloadAsync();
            item2.Quantity = 85;
            await context2.SaveChangesAsync();

            // Verify the resolved value
            await using var verifyContext = fixture.CreateContext();
            var updated = await verifyContext.Inventory.FindAsync(inventoryId);
            Assert.NotNull(updated);
            Assert.Equal(85, updated.Quantity);
        }
    }

    [Fact]
    public async Task RowVersion_UpdatesOnEachSave()
    {
        await fixture.ResetDataAsync();

        int inventoryId;
        byte[]? firstVersion;

        await using (var context = fixture.CreateContext())
        {
            var inventory = new InventoryItem { ProductName = "Widget", Quantity = 100 };
            context.Inventory.Add(inventory);
            await context.SaveChangesAsync();
            inventoryId = inventory.Id;
            firstVersion = inventory.RowVersion;
        }

        await using (var context = fixture.CreateContext())
        {
            var item = await context.Inventory.FindAsync(inventoryId);
            Assert.NotNull(item);

            var secondVersion = item.RowVersion;
            Assert.NotNull(secondVersion);
            Assert.NotEqual(firstVersion, secondVersion);

            item.Quantity = 90;
            await context.SaveChangesAsync();

            var thirdVersion = item.RowVersion;
            Assert.NotNull(thirdVersion);
            Assert.NotEqual(secondVersion, thirdVersion);
        }
    }

    public sealed class PostgresFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container;
        private string connectionString = string.Empty;

        public PostgresFixture()
        {
            container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("themia_concurrency_tests")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .WithCleanUp(true)
                .Build();
        }

        public async Task InitializeAsync()
        {
            await container.StartAsync();
            connectionString = container.GetConnectionString();
            await EnsureSchemaAsync();
        }

        public async Task DisposeAsync() => await container.DisposeAsync();

        public TestConcurrencyDbContext CreateContext() =>
            new(GetOptions());

        public async Task ResetDataAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
            await context.Inventory.ExecuteDeleteAsync();
        }

        private DbContextOptions<TestConcurrencyDbContext> GetOptions()
        {
            return new DbContextOptionsBuilder<TestConcurrencyDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options;
        }

        private async Task EnsureSchemaAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }
    }

    public class TestConcurrencyDbContext : ThemiaDbContext
    {
        public TestConcurrencyDbContext(DbContextOptions options)
            : base(options, null, null)
        {
        }

        public DbSet<InventoryItem> Inventory => Set<InventoryItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<InventoryItem>(entity =>
            {
                entity.ToTable("inventory_items");
                entity.HasKey(i => i.Id);
                entity.Property(i => i.ProductName).IsRequired().HasMaxLength(200);
            });
            base.OnModelCreating(modelBuilder);
        }
    }

    public class InventoryItem : IConcurrencyAware
    {
        public int Id { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public byte[]? RowVersion { get; set; }
    }
}
