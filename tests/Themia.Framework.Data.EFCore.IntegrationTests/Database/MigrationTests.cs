using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Data.EFCore.IntegrationTests.Database;

/// <summary>
/// Integration tests for database migrations and schema validation.
/// </summary>
[Trait("Category", "Integration")]
public class MigrationTests : IClassFixture<MigrationTests.PostgresFixture>
{
    private readonly PostgresFixture fixture;

    public MigrationTests(PostgresFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task EnsureCreated_CreatesAllTables()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        // Verify migration_products table exists
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'public'
            AND table_name = 'migration_products';
        ";

        var tableCount = (long)(await command.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(1, tableCount);
    }

    [Fact]
    public async Task Schema_HasAllExpectedColumns()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT column_name, data_type, is_nullable
            FROM information_schema.columns
            WHERE table_name = 'migration_products'
            ORDER BY ordinal_position;
        ";

        var columns = new System.Collections.Generic.Dictionary<string, (string DataType, string IsNullable)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var columnName = reader.GetString(0);
            var dataType = reader.GetString(1);
            var isNullable = reader.GetString(2);
            columns[columnName] = (dataType, isNullable);
        }

        // Verify key columns exist with correct types
        Assert.True(columns.ContainsKey("id"));
        Assert.Equal("integer", columns["id"].DataType);
        Assert.Equal("NO", columns["id"].IsNullable);

        Assert.True(columns.ContainsKey("name"));
        // HasMaxLength(200) maps to character varying; unbounded strings map to text in EF Core 10 GA.
        Assert.Equal("character varying", columns["name"].DataType);
        Assert.Equal("NO", columns["name"].IsNullable);

        Assert.True(columns.ContainsKey("price"));
        Assert.Equal("numeric", columns["price"].DataType);
        Assert.Equal("NO", columns["price"].IsNullable);

        Assert.True(columns.ContainsKey("tenant_id"));
        // Unbounded string (TenantId value converter, no HasMaxLength) → PostgreSQL text on EF Core 10 GA + Npgsql.
        Assert.Equal("text", columns["tenant_id"].DataType);
        Assert.Equal("YES", columns["tenant_id"].IsNullable);

        Assert.True(columns.ContainsKey("created_at"));
        Assert.Contains("timestamp", columns["created_at"].DataType);
        Assert.Equal("NO", columns["created_at"].IsNullable);

        Assert.True(columns.ContainsKey("row_version"));
        // byte[] + IsRowVersion() maps to PostgreSQL bytea on Npgsql (not timestamp).
        Assert.Equal("bytea", columns["row_version"].DataType);
    }

    [Fact]
    public async Task Schema_HasPrimaryKeyConstraint()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT constraint_name, constraint_type
            FROM information_schema.table_constraints
            WHERE table_name = 'migration_products'
            AND constraint_type = 'PRIMARY KEY';
        ";

        var hasPrimaryKey = false;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            hasPrimaryKey = true;
            var constraintType = reader.GetString(1);
            Assert.Equal("PRIMARY KEY", constraintType);
        }

        Assert.True(hasPrimaryKey, "Primary key constraint not found");
    }

    [Fact]
    public async Task Schema_HasUniqueConstraintOnSku()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT indexdef
            FROM pg_indexes
            WHERE tablename = 'migration_products'
            AND indexdef LIKE '%UNIQUE%'
            AND indexdef LIKE '%sku%';
        ";

        var hasUniqueIndex = false;
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            hasUniqueIndex = true;
            var indexDef = reader.GetString(0);
            Assert.Contains("UNIQUE", indexDef);
            Assert.Contains("sku", indexDef);
        }

        Assert.True(hasUniqueIndex, "Unique index on SKU not found");
    }

    [Fact]
    public async Task Schema_HasIndexOnTenantId()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT indexname, indexdef
            FROM pg_indexes
            WHERE tablename = 'migration_products'
            AND indexdef LIKE '%tenant_id%';
        ";

        var hasIndex = false;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            hasIndex = true;
            var indexDef = reader.GetString(1);
            Assert.Contains("tenant_id", indexDef);
        }

        Assert.True(hasIndex, "Index on tenant_id not found");
    }

    [Fact]
    public async Task EnsureDeleted_RemovesDatabase()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        // Ensure created first (ResetDataAsync already ran EnsureCreated, so this returns false)
        await context.Database.EnsureCreatedAsync();

        // EnsureDeletedAsync drops the entire database and returns true.
        // After the database is gone, the connection string is no longer usable until recreated.
        var deleted = await context.Database.EnsureDeletedAsync();
        Assert.True(deleted);

        // Recreate so that subsequent tests / fixture teardown work correctly.
        await context.Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task CanRetrieveAndUpdateRecord_AfterMigration()
    {
        await fixture.ResetDataAsync();

        var productId = 0;
        var tenantId = new TenantId("test-tenant");

        // Create — pass tenant so the query filter lets the row through on subsequent reads.
        await using (var context = fixture.CreateContext(tenantId))
        {
            var product = new MigrationProduct
            {
                Name = "Test Product",
                Sku = "TEST-SKU",
                Price = 99.99m,
                TenantId = tenantId
            };

            context.Products.Add(product);
            await context.SaveChangesAsync();
            productId = product.Id;

            Assert.True(productId > 0);
        }

        // Read and Update
        await using (var context = fixture.CreateContext(tenantId))
        {
            var product = await context.Products.FindAsync(productId);
            Assert.NotNull(product);
            Assert.Equal("Test Product", product.Name);

            product.Price = 149.99m;
            await context.SaveChangesAsync();
        }

        // Verify Update
        await using (var context = fixture.CreateContext(tenantId))
        {
            var product = await context.Products.FindAsync(productId);
            Assert.NotNull(product);
            Assert.Equal(149.99m, product.Price);
        }
    }

    [Fact]
    public async Task MultipleContexts_CanAccessSameDatabase()
    {
        await fixture.ResetDataAsync();

        var tenantId = new TenantId("test-tenant");

        // Context 1 creates — tenant context required so fail-closed filter lets the row through on reads.
        await using (var context1 = fixture.CreateContext(tenantId))
        {
            context1.Products.Add(new MigrationProduct
            {
                Name = "Product 1",
                Sku = "SKU-001",
                Price = 50m,
                TenantId = tenantId
            });
            await context1.SaveChangesAsync();
        }

        // Context 2 reads — same tenant so the row is visible.
        await using (var context2 = fixture.CreateContext(tenantId))
        {
            var products = await context2.Products.ToListAsync();
            Assert.Single(products);
            Assert.Equal("Product 1", products[0].Name);
        }
    }

    public sealed class PostgresFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container;
        private string connectionString = string.Empty;

        public PostgresFixture()
        {
            container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("themia_migration_tests")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .WithCleanUp(true)
                .Build();
        }

        public async Task InitializeAsync()
        {
            await container.StartAsync();
            connectionString = container.GetConnectionString();
        }

        public async Task DisposeAsync() => await container.DisposeAsync();

        public TestMigrationDbContext CreateContext(TenantId? tenantId = null) =>
            new(GetOptions(), tenantId != null ? new TenantContext(tenantId) : null);

        public async Task ResetDataAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
        }

        private DbContextOptions<TestMigrationDbContext> GetOptions()
        {
            return new DbContextOptionsBuilder<TestMigrationDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options;
        }
    }

    public class TestMigrationDbContext : ThemiaDbContext
    {
        public TestMigrationDbContext(DbContextOptions options, ITenantContext? tenantContext = null)
            : base(options, tenantContext, null)
        {
        }

        public DbSet<MigrationProduct> Products => Set<MigrationProduct>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MigrationProduct>(entity =>
            {
                entity.ToTable("migration_products");
                entity.HasKey(p => p.Id);
                entity.Property(p => p.Name).IsRequired().HasMaxLength(200);
                entity.Property(p => p.Sku).IsRequired().HasMaxLength(50);
                entity.Property(p => p.TenantId);
                entity.HasIndex(p => p.Sku).IsUnique();
                entity.HasIndex(p => p.TenantId);
                entity.Property(p => p.Price).HasPrecision(18, 2);
            });
            base.OnModelCreating(modelBuilder);
        }
    }

    public class MigrationProduct : IAuditableEntity, IConcurrencyAware, ITenantEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Sku { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public TenantId? TenantId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset? LastModifiedAt { get; set; }
        public string? LastModifiedBy { get; set; }
        public byte[]? RowVersion { get; set; }
    }
}
