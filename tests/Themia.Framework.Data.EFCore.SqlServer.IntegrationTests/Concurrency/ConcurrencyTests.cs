using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.Concurrency;

/// <summary>
/// Integration tests for optimistic concurrency control with real SQL Server.
/// SQL Server uses a server-maintained <c>rowversion</c> column — correct for <c>byte[] IsRowVersion()</c>.
/// </summary>
[Trait("Category", "Integration")]
public class ConcurrencyTests : IClassFixture<ConcurrencyTests.SqlServerFixture>
{
    private readonly SqlServerFixture fixture;

    public ConcurrencyTests(SqlServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Insert_SetsRowVersion()
    {
        await fixture.ResetDataAsync();

        int inventoryId;
        await using (var context = fixture.CreateContext())
        {
            var inventory = new InventoryItem { ProductName = "Widget", Quantity = 100 };
            context.Inventory.Add(inventory);
            await context.SaveChangesAsync();
            inventoryId = inventory.Id;
            Assert.True(inventoryId > 0);
        }

        await using (var context = fixture.CreateContext())
        {
            var item = await context.Inventory.FindAsync(inventoryId);
            Assert.NotNull(item);
            Assert.Equal("Widget", item.ProductName);
            Assert.Equal(100, item.Quantity);

            item.Quantity = 90;
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            var item = await context.Inventory.FindAsync(inventoryId);
            Assert.NotNull(item);
            Assert.Equal(90, item.Quantity);
        }
    }

    /// <summary>
    /// Verifies that a real concurrency conflict throws <see cref="DbUpdateConcurrencyException"/>.
    /// Two contexts load the same row; context1 saves first, incrementing the SQL Server rowversion;
    /// context2 then tries to save with the stale rowversion and must fail.
    /// </summary>
    [Fact]
    public async Task Concurrency_ConflictingSave_ThrowsDbUpdateConcurrencyException()
    {
        await fixture.ResetDataAsync();

        // Arrange: insert a row.
        int itemId;
        await using (var ctx = fixture.CreateContext())
        {
            var item = new InventoryItem { ProductName = "Gadget", Quantity = 50 };
            ctx.Inventory.Add(item);
            await ctx.SaveChangesAsync();
            itemId = item.Id;
        }

        // Load the same row into two separate contexts.
        await using var context1 = fixture.CreateContext();
        await using var context2 = fixture.CreateContext();

        var item1 = await context1.Inventory.FindAsync(itemId);
        var item2 = await context2.Inventory.FindAsync(itemId);
        Assert.NotNull(item1);
        Assert.NotNull(item2);

        // Act: context1 saves first — increments the SQL Server rowversion.
        item1.Quantity = 40;
        await context1.SaveChangesAsync();

        // context2 still holds the old rowversion — its save must throw.
        item2.Quantity = 30;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => context2.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrency_NoConflict_Succeeds()
    {
        await fixture.ResetDataAsync();

        int itemId;
        await using (var ctx = fixture.CreateContext())
        {
            var item = new InventoryItem { ProductName = "Gadget", Quantity = 50 };
            ctx.Inventory.Add(item);
            await ctx.SaveChangesAsync();
            itemId = item.Id;
        }

        // Two sequential updates in independent contexts — no conflict.
        await using (var ctx = fixture.CreateContext())
        {
            var item = await ctx.Inventory.FindAsync(itemId);
            Assert.NotNull(item);
            item.Quantity = 45;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = fixture.CreateContext())
        {
            var item = await ctx.Inventory.FindAsync(itemId);
            Assert.NotNull(item);
            item.Quantity = 40;
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = fixture.CreateContext())
        {
            var item = await ctx.Inventory.FindAsync(itemId);
            Assert.NotNull(item);
            Assert.Equal(40, item.Quantity);
        }
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    public sealed class SqlServerFixture : IAsyncLifetime
    {
        private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithCleanUp(true)
            .Build();

        private string connectionString = string.Empty;

        public async Task InitializeAsync()
        {
            await container.StartAsync();
            connectionString = container.GetConnectionString();
            await EnsureSchemaAsync();
        }

        public async Task DisposeAsync() => await container.DisposeAsync();

        public TestConcurrencyDbContext CreateContext() => new(GetOptions());

        public async Task ResetDataAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
            await context.Inventory.ExecuteDeleteAsync();
        }

        private DbContextOptions<TestConcurrencyDbContext> GetOptions() =>
            new DbContextOptionsBuilder<TestConcurrencyDbContext>()
                .UseSqlServer(connectionString)
                .Options;

        private async Task EnsureSchemaAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }
    }

    // ── Test context ──────────────────────────────────────────────────────────

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

    // ── Entity ────────────────────────────────────────────────────────────────

    public class InventoryItem : IConcurrencyAware
    {
        public int Id { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public byte[]? RowVersion { get; set; }
    }
}
