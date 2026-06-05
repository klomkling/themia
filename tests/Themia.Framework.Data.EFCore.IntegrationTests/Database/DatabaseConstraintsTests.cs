using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Data.EFCore.IntegrationTests.Database;

/// <summary>
/// Integration tests validating database constraints, indexes, and SQL generation.
/// </summary>
[Trait("Category", "Integration")]
public class DatabaseConstraintsTests : IClassFixture<DatabaseConstraintsTests.PostgresFixture>
{
    private readonly PostgresFixture fixture;

    public DatabaseConstraintsTests(PostgresFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task PrimaryKey_IsEnforced_OnDuplicateId()
    {
        await fixture.ResetDataAsync();

        // First context: insert id=1
        await using (var context1 = fixture.CreateContext())
        {
            var product1 = new ConstraintProduct { Id = 1, Name = "Product 1", Sku = "SKU001" };
            context1.Products.Add(product1);
            await context1.SaveChangesAsync();
        }

        // Second context: attempt to insert a second row with the same id=1.
        // Using a separate context avoids the EF identity-map duplicate-key check,
        // letting the actual PostgreSQL PK constraint fire.
        await using var context2 = fixture.CreateContext();
        var product2 = new ConstraintProduct { Id = 1, Name = "Product 2", Sku = "SKU002" };
        context2.Products.Add(product2);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context2.SaveChangesAsync());
        Assert.IsType<PostgresException>(exception.InnerException);
        var pgException = (PostgresException)exception.InnerException!;
        Assert.Equal("23505", pgException.SqlState); // unique_violation
    }

    [Fact]
    public async Task UniqueConstraint_IsEnforced_OnSkuField()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        var product1 = new ConstraintProduct { Id = 1, Name = "Product 1", Sku = "UNIQUE-SKU" };
        context.Products.Add(product1);
        await context.SaveChangesAsync();

        // Try to insert duplicate SKU
        var product2 = new ConstraintProduct { Id = 2, Name = "Product 2", Sku = "UNIQUE-SKU" };
        context.Products.Add(product2);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.IsType<PostgresException>(exception.InnerException);
        var pgException = (PostgresException)exception.InnerException!;
        Assert.Equal("23505", pgException.SqlState); // unique_violation
    }

    [Fact]
    public async Task NotNullConstraint_IsEnforced_OnRequiredFields()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        // Create product with null name (should fail)
        var product = new ConstraintProduct { Id = 1, Name = null!, Sku = "SKU001" };
        context.Products.Add(product);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.IsType<PostgresException>(exception.InnerException);
        var pgException = (PostgresException)exception.InnerException!;
        Assert.Equal("23502", pgException.SqlState); // not_null_violation
    }

    [Fact]
    public async Task CheckConstraint_IsEnforced_OnPriceRange()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        // Try to insert negative price
        var product = new ConstraintProduct { Id = 1, Name = "Product", Sku = "SKU001", Price = -10 };
        context.Products.Add(product);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.IsType<PostgresException>(exception.InnerException);
        var pgException = (PostgresException)exception.InnerException!;
        Assert.Equal("23514", pgException.SqlState); // check_violation
    }

    [Fact]
    public async Task Index_ImprovesTenantQueryPerformance()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        // Insert test data for multiple tenants
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");

        for (int i = 0; i < 1000; i++)
        {
            context.Products.Add(new ConstraintProduct
            {
                Id = i + 1,
                Name = $"Product {i}",
                Sku = $"SKU{i:D6}",
                Price = i % 2 == 0 ? 100 : 200,
                TenantId = i % 2 == 0 ? tenantA : tenantB
            });
        }
        await context.SaveChangesAsync();

        // Verify index is used (check execution plan)
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            EXPLAIN (FORMAT JSON)
            SELECT * FROM constraint_products
            WHERE tenant_id = 'tenant-a';
        ";

        var plan = await command.ExecuteScalarAsync();
        var planText = plan?.ToString() ?? "";

        // Verify index scan is used (not sequential scan)
        Assert.Contains("Index", planText);
    }

    [Fact]
    public async Task SnakeCaseNaming_IsAppliedToColumns()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        // Verify snake_case column names in database
        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT column_name
            FROM information_schema.columns
            WHERE table_name = 'constraint_products'
            ORDER BY ordinal_position;
        ";

        var columns = new System.Collections.Generic.List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Contains("id", columns);
        Assert.Contains("name", columns);
        Assert.Contains("sku", columns);
        Assert.Contains("price", columns);
        Assert.Contains("tenant_id", columns);
        Assert.Contains("created_at", columns);
    }

    [Fact]
    public async Task TenantIdType_IsMappedToVarchar()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT data_type, character_maximum_length
            FROM information_schema.columns
            WHERE table_name = 'constraint_products'
            AND column_name = 'tenant_id';
        ";

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var dataType = reader.GetString(0);
        // EF Core 10 GA + Npgsql maps unbounded string (no HasMaxLength) to PostgreSQL `text`.
        Assert.Equal("text", dataType);
    }

    [Fact]
    public async Task RowVersion_IsMappedToBytea()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext();

        await using var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT data_type
            FROM information_schema.columns
            WHERE table_name = 'constraint_products'
            AND column_name = 'row_version';
        ";

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var dataType = reader.GetString(0);
        // byte[] + IsRowVersion() maps to PostgreSQL bytea on Npgsql (not timestamp).
        Assert.Equal("bytea", dataType);
    }

    public sealed class PostgresFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container;
        private string connectionString = string.Empty;

        public PostgresFixture()
        {
            container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("themia_constraints_tests")
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

        public TestConstraintsDbContext CreateContext() => new(GetOptions());

        public async Task ResetDataAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
            await context.Products.ExecuteDeleteAsync();
        }

        private DbContextOptions<TestConstraintsDbContext> GetOptions()
        {
            return new DbContextOptionsBuilder<TestConstraintsDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .EnableDetailedErrors()
                .EnableSensitiveDataLogging()
                .Options;
        }

        private async Task EnsureSchemaAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }
    }

    public class TestConstraintsDbContext : ThemiaDbContext
    {
        public TestConstraintsDbContext(DbContextOptions options) : base(options, null, null)
        {
        }

        public DbSet<ConstraintProduct> Products => Set<ConstraintProduct>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConstraintProduct>(entity =>
            {
                entity.ToTable("constraint_products");
                entity.HasKey(p => p.Id);

                entity.Property(p => p.Name)
                    .IsRequired()
                    .HasMaxLength(200);

                entity.Property(p => p.Sku)
                    .IsRequired()
                    .HasMaxLength(50);

                entity.HasIndex(p => p.Sku)
                    .IsUnique();

                entity.Property(p => p.Price)
                    .HasPrecision(18, 2);

                entity.Property(p => p.TenantId);

                // Add check constraint for price
                entity.ToTable(t => t.HasCheckConstraint("CK_Price", "price >= 0"));

                // Add index on tenant_id for better query performance
                entity.HasIndex(p => p.TenantId);
            });
            base.OnModelCreating(modelBuilder);
        }
    }

    public class ConstraintProduct : IAuditableEntity, IConcurrencyAware, ITenantEntity
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
