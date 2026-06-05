using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Framework.Core.Abstractions.Entities;
using Xunit;

namespace Themia.Framework.Data.EFCore.IntegrationTests.Concurrency;

/// <summary>
/// Integration tests for optimistic concurrency control with real PostgreSQL.
/// </summary>
/// <remarks>
/// On Npgsql (PostgreSQL), <c>byte[] IsRowVersion()</c> maps to <c>bytea</c> which is not
/// server-populated — EF Core never sees a changed value, so <c>DbUpdateConcurrencyException</c>
/// would never fire. Themia fixes this by adding a shadow <c>uint</c> property configured as
/// <c>IsRowVersion()</c>; Npgsql's convention then maps it to the system column <c>xmin</c>,
/// which PostgreSQL increments on every row write. The <c>byte[] RowVersion</c> column is kept
/// mapped as a plain column for schema compatibility; dropping it is a deferred cleanup.
/// </remarks>
[Trait("Category", "Integration")]
public class ConcurrencyIntegrationTests : IClassFixture<ConcurrencyIntegrationTests.PostgresFixture>
{
    private readonly PostgresFixture fixture;

    public ConcurrencyIntegrationTests(PostgresFixture fixture)
    {
        this.fixture = fixture;
    }

    /// <summary>
    /// Verifies that the shadow <c>uint xmin</c> property is configured as the concurrency token
    /// in the EF Core model on PostgreSQL, and that <c>byte[] RowVersion</c> is NOT the token
    /// (it is kept mapped as a plain column for schema compatibility).
    /// </summary>
    [Fact]
    public async Task Xmin_IsConfiguredAsConcurrencyToken_InModel()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        var entityType = context.Model.FindEntityType(typeof(InventoryItem));
        Assert.NotNull(entityType);

        // xmin shadow property is the concurrency token on Npgsql.
        var xminProperty = entityType.FindProperty("xmin");
        Assert.NotNull(xminProperty);
        Assert.True(xminProperty.IsConcurrencyToken, "xmin shadow property should be configured as a concurrency token on PostgreSQL.");
        Assert.Equal(typeof(uint), xminProperty.ClrType);

        // byte[] RowVersion stays mapped as a plain column, NOT as the concurrency token.
        var rowVersionProperty = entityType.FindProperty(nameof(InventoryItem.RowVersion));
        Assert.NotNull(rowVersionProperty);
        Assert.False(rowVersionProperty.IsConcurrencyToken, "byte[] RowVersion should NOT be the concurrency token on PostgreSQL (it is a plain column).");
    }

    /// <summary>
    /// Verifies that the <c>row_version</c> column is still present in the database as <c>bytea</c>.
    /// The column is kept mapped for schema compatibility; it is NOT the active concurrency token
    /// on PostgreSQL (xmin is). Removing it is a deferred migration-level cleanup.
    /// </summary>
    [Fact]
    public async Task RowVersion_IsMappedToBytea_InDatabase()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();
        await using var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT data_type
            FROM information_schema.columns
            WHERE table_name = 'inventory_items'
            AND column_name = 'row_version';
        ";

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "row_version column should exist in inventory_items.");

        var dataType = reader.GetString(0);
        Assert.Equal("bytea", dataType);
    }

    /// <summary>
    /// Verifies that basic CRUD operations work correctly with the xmin-based concurrency setup.
    /// </summary>
    [Fact]
    public async Task InventoryItem_CanBeSavedAndRetrieved()
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
    /// Verifies that a real concurrency conflict throws <see cref="DbUpdateConcurrencyException"/>
    /// when two contexts load the same row and each tries to save a conflicting update.
    /// Before the xmin fix this save would silently succeed (bytea was never server-updated).
    /// After the fix, PostgreSQL's xmin increments on the first save, making the second save fail.
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

        // Act: context1 saves first — increments xmin.
        item1.Quantity = 40;
        await context1.SaveChangesAsync();

        // context2 still holds the old xmin value — its save must fail.
        item2.Quantity = 30;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => context2.SaveChangesAsync());
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
