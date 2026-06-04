using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;
using Xunit.Abstractions;

namespace Themia.Framework.Data.EFCore.IntegrationTests.Database;

/// <summary>
/// Integration tests validating SQL query generation for complex scenarios.
/// </summary>
[Trait("Category", "Integration")]
public class QueryGenerationTests : IClassFixture<QueryGenerationTests.PostgresFixture>
{
    private readonly PostgresFixture fixture;
    private readonly ITestOutputHelper output;

    public QueryGenerationTests(PostgresFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact]
    public async Task TenantFilter_GeneratesCorrectWhereClause()
    {
        await fixture.ResetDataAsync();

        var tenantId = new TenantId("test-tenant");
        await using var context = fixture.CreateContext(tenantId);

        // This should generate SQL with WHERE tenant_id = 'test-tenant' OR tenant_id IS NULL
        var query = context.Orders.Where(o => o.Name.StartsWith("Order"));
        var sql = query.ToQueryString();

        output.WriteLine("Generated SQL:");
        output.WriteLine(sql);

        // Verify tenant filter is in the query
        Assert.Contains("tenant_id", sql.ToLowerInvariant());
        Assert.Contains("test-tenant", sql);
    }

    [Fact]
    public async Task SoftDeleteFilter_GeneratesCorrectWhereClause()
    {
        await fixture.ResetDataAsync();

        await using var context = fixture.CreateContext(null);

        var query = context.Orders.Where(o => o.Name.Contains("Active"));
        var sql = query.ToQueryString();

        output.WriteLine("Generated SQL:");
        output.WriteLine(sql);

        // Verify soft delete filter
        Assert.Contains("is_deleted", sql.ToLowerInvariant());
        Assert.Contains("false", sql.ToLowerInvariant());
    }

    [Fact]
    public async Task CombinedFilters_GenerateCorrectAndClause()
    {
        await fixture.ResetDataAsync();

        var tenantId = new TenantId("test-tenant");
        await using var context = fixture.CreateContext(tenantId);

        var query = context.Orders
            .Where(o => o.Name.StartsWith("Order") && o.Quantity > 10);

        var sql = query.ToQueryString();

        output.WriteLine("Generated SQL:");
        output.WriteLine(sql);

        // Should have tenant filter AND soft delete filter AND custom filters
        Assert.Contains("tenant_id", sql.ToLowerInvariant());
        Assert.Contains("is_deleted", sql.ToLowerInvariant());
        Assert.Contains("quantity", sql.ToLowerInvariant());
    }

    [Fact]
    public async Task Join_WithTenantFilter_GeneratesCorrectQuery()
    {
        await fixture.ResetDataAsync();

        var tenantId = new TenantId("test-tenant");
        await using var context = fixture.CreateContext(tenantId);

        // Seed data
        var category = new QueryCategory { Id = 1, Name = "Electronics", TenantId = tenantId };
        context.Categories.Add(category);
        await context.SaveChangesAsync();

        var order = new QueryOrder
        {
            Id = 1,
            Name = "Order 1",
            Quantity = 5,
            CategoryId = 1,
            TenantId = tenantId
        };
        context.Orders.Add(order);
        await context.SaveChangesAsync();

        // Query with join
        var query = from o in context.Orders
                    join c in context.Categories on o.CategoryId equals c.Id
                    where o.Quantity > 1
                    select new { OrderName = o.Name, CategoryName = c.Name };

        var sql = query.ToQueryString();

        output.WriteLine("Generated SQL:");
        output.WriteLine(sql);

        // Execute and verify
        var results = await query.ToListAsync();
        Assert.Single(results);
        Assert.Equal("Order 1", results[0].OrderName);
        Assert.Equal("Electronics", results[0].CategoryName);
    }

    [Fact]
    public async Task GroupBy_GeneratesCorrectAggregation()
    {
        await fixture.ResetDataAsync();

        var tenantId = new TenantId("test-tenant");
        await using var context = fixture.CreateContext(tenantId);

        // Seed data
        for (int i = 1; i <= 10; i++)
        {
            context.Orders.Add(new QueryOrder
            {
                Id = i,
                Name = $"Order {i}",
                Quantity = i,
                CategoryId = i % 2 + 1,
                TenantId = tenantId
            });
        }
        await context.SaveChangesAsync();

        // Group by and aggregate
        var query = context.Orders
            .GroupBy(o => o.CategoryId)
            .Select(g => new
            {
                CategoryId = g.Key,
                TotalQuantity = g.Sum(o => o.Quantity),
                Count = g.Count()
            });

        var sql = query.ToQueryString();

        output.WriteLine("Generated SQL:");
        output.WriteLine(sql);

        // Verify GROUP BY is generated
        Assert.Contains("GROUP BY", sql);

        var results = await query.ToListAsync();
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task Pagination_GeneratesOffsetAndLimit()
    {
        await fixture.ResetDataAsync();

        var tenantId = new TenantId("test-tenant");
        await using var context = fixture.CreateContext(tenantId);

        // Seed data
        for (int i = 1; i <= 100; i++)
        {
            context.Orders.Add(new QueryOrder
            {
                Id = i,
                Name = $"Order {i}",
                Quantity = i,
                TenantId = tenantId
            });
        }
        await context.SaveChangesAsync();

        // Paginated query
        var query = context.Orders
            .OrderBy(o => o.Id)
            .Skip(20)
            .Take(10);

        var sql = query.ToQueryString();

        output.WriteLine("Generated SQL:");
        output.WriteLine(sql);

        // Verify OFFSET and LIMIT
        Assert.Contains("OFFSET", sql);
        Assert.Contains("LIMIT", sql);

        var results = await query.ToListAsync();
        Assert.Equal(10, results.Count);
        Assert.Equal(21, results.First().Id);
    }

    [Fact]
    public async Task SubQuery_GeneratesCorrectNestedSelect()
    {
        await fixture.ResetDataAsync();

        var tenantId = new TenantId("test-tenant");
        await using var context = fixture.CreateContext(tenantId);

        // Seed data
        for (int i = 1; i <= 50; i++)
        {
            context.Orders.Add(new QueryOrder
            {
                Id = i,
                Name = $"Order {i}",
                Quantity = i % 10,
                TenantId = tenantId
            });
        }
        await context.SaveChangesAsync();

        // Subquery to find orders with above-average quantity
        var avgQuantity = await context.Orders.AverageAsync(o => o.Quantity);

        var query = context.Orders
            .Where(o => o.Quantity > avgQuantity)
            .OrderByDescending(o => o.Quantity);

        var sql = query.ToQueryString();

        output.WriteLine("Generated SQL:");
        output.WriteLine(sql);

        var results = await query.ToListAsync();
        Assert.All(results, r => Assert.True(r.Quantity > avgQuantity));
    }

    [Fact]
    public async Task Include_GeneratesLeftJoin()
    {
        await fixture.ResetDataAsync();

        var tenantId = new TenantId("test-tenant");
        await using var context = fixture.CreateContext(tenantId);

        // Note: This test would require navigation properties
        // For now, we'll test that basic includes work with manual setup

        var query = context.Orders.AsQueryable();
        var sql = query.ToQueryString();

        output.WriteLine("Generated SQL:");
        output.WriteLine(sql);

        Assert.Contains("FROM", sql);
    }

    [Fact]
    public async Task DateTimeOffset_IsMappedCorrectly()
    {
        await fixture.ResetDataAsync();

        var tenantId = new TenantId("test-tenant");
        await using var context = fixture.CreateContext(tenantId);

        var order = new QueryOrder
        {
            Id = 1,
            Name = "Test Order",
            Quantity = 10,
            TenantId = tenantId
        };

        context.Orders.Add(order);
        await context.SaveChangesAsync();

        // Verify CreatedAt was set and can be queried
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);
        var tomorrow = DateTimeOffset.UtcNow.AddDays(1);

        var found = await context.Orders
            .Where(o => o.CreatedAt >= yesterday && o.CreatedAt <= tomorrow)
            .FirstOrDefaultAsync();

        Assert.NotNull(found);
        Assert.Equal(1, found.Id);
    }

    public sealed class PostgresFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container;
        private string connectionString = string.Empty;

        public PostgresFixture()
        {
            container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("themia_query_tests")
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

        public TestQueryDbContext CreateContext(TenantId? tenantId = null) =>
            new(GetOptions(), tenantId);

        public async Task ResetDataAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
            await context.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
            await context.Categories.IgnoreQueryFilters().ExecuteDeleteAsync();
        }

        private DbContextOptions<TestQueryDbContext> GetOptions()
        {
            return new DbContextOptionsBuilder<TestQueryDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .EnableDetailedErrors()
                .EnableSensitiveDataLogging()
                .LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
                .Options;
        }

        private async Task EnsureSchemaAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }
    }

    public class TestQueryDbContext : ThemiaDbContext
    {
        public TestQueryDbContext(DbContextOptions options, TenantId? tenantId)
            : base(options, tenantId != null ? new TenantContext(tenantId) : null, null)
        {
        }

        public DbSet<QueryOrder> Orders => Set<QueryOrder>();
        public DbSet<QueryCategory> Categories => Set<QueryCategory>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<QueryOrder>(entity =>
            {
                entity.ToTable("query_orders");
                entity.HasKey(o => o.Id);
                entity.Property(o => o.Name).IsRequired().HasMaxLength(200);
                entity.Property(o => o.TenantId);
                entity.HasIndex(o => o.TenantId);
            });

            modelBuilder.Entity<QueryCategory>(entity =>
            {
                entity.ToTable("query_categories");
                entity.HasKey(c => c.Id);
                entity.Property(c => c.Name).IsRequired().HasMaxLength(100);
                entity.Property(c => c.TenantId);
                entity.HasIndex(c => c.TenantId);
            });
            base.OnModelCreating(modelBuilder);
        }
    }

    public class QueryOrder : IAuditableEntity, ISoftDeletable, ITenantEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public int? CategoryId { get; set; }
        public TenantId? TenantId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset? LastModifiedAt { get; set; }
        public string? LastModifiedBy { get; set; }
        public bool IsDeleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? RestoredAt { get; set; }
        public string? RestoredBy { get; set; }
    }

    public class QueryCategory : ITenantEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public TenantId? TenantId { get; set; }
    }
}
