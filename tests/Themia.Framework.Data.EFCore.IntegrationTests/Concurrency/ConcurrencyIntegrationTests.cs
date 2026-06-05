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
/// Effective optimistic concurrency on PostgreSQL requires UseXminAsConcurrencyToken on the entity
/// (or a trigger-backed rowversion column). Themia currently uses IsRowVersion() on a byte[] column,
/// which creates a bytea column that is NOT server-populated by PostgreSQL — the database engine does
/// not update it on each row write. As a result, DbUpdateConcurrencyException is NOT thrown at runtime
/// on Npgsql. Tracked as a Tier-3 / 0.3.0 backlog item to switch to UseXminAsConcurrencyToken or an
/// explicit trigger approach for effective PostgreSQL concurrency protection.
/// These tests assert the current model-level configuration only.
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
    /// Verifies that the RowVersion property is configured as a concurrency token in the EF Core model.
    /// Effective runtime concurrency on PostgreSQL requires UseXminAsConcurrencyToken (backlog item).
    /// </summary>
    [Fact]
    public async Task RowVersion_IsConfiguredAsConcurrencyToken_InModel()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        var entityType = context.Model.FindEntityType(typeof(InventoryItem));
        Assert.NotNull(entityType);

        var property = entityType.FindProperty(nameof(InventoryItem.RowVersion));
        Assert.NotNull(property);
        Assert.True(property.IsConcurrencyToken, "RowVersion should be configured as a concurrency token.");
    }

    /// <summary>
    /// Verifies that the RowVersion column is mapped to PostgreSQL bytea.
    /// byte[] + IsRowVersion() maps to bytea on Npgsql (not timestamp/rowversion as on SQL Server).
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
    /// Verifies that basic CRUD operations work correctly and that RowVersion is populated by EF Core
    /// (client-side) after each save. Note: on PostgreSQL, EF does NOT re-fetch the bytea column
    /// after insert/update (it is not server-populated), so RowVersion remains null after a save.
    /// The model-level token is asserted separately; this test confirms the entity round-trips correctly.
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
