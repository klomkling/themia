using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Themia.Framework.Core.Abstractions.Entities;
using Xunit;

namespace Themia.Framework.Data.EFCore.IntegrationTests.Audit;

/// <summary>
/// Integration tests for audit tracking functionality with real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public class AuditTrackingIntegrationTests : IClassFixture<AuditTrackingIntegrationTests.PostgresFixture>
{
    private readonly PostgresFixture fixture;

    public AuditTrackingIntegrationTests(PostgresFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task SaveChanges_SetsCreatedAt_OnNewEntity()
    {
        await fixture.ResetDataAsync();

        var beforeCreate = DateTimeOffset.UtcNow;

        await using (var context = fixture.CreateContext())
        {
            var product = new AuditableProduct { Name = "Test Product", Price = 99.99m };
            context.Products.Add(product);
            await context.SaveChangesAsync();

            var afterCreate = DateTimeOffset.UtcNow;

            Assert.True(product.CreatedAt >= beforeCreate);
            Assert.True(product.CreatedAt <= afterCreate);
            Assert.Null(product.LastModifiedAt);
        }
    }

    [Fact]
    public async Task SaveChanges_SetsCreatedBy_WhenUserIdProvided()
    {
        await fixture.ResetDataAsync();

        await using (var context = fixture.CreateContextWithUser("user-123"))
        {
            var product = new AuditableProduct { Name = "Test Product", Price = 99.99m };
            context.Products.Add(product);
            await context.SaveChangesAsync();

            Assert.Equal("user-123", product.CreatedBy);
        }
    }

    [Fact]
    public async Task SaveChanges_SetsLastModifiedAt_OnUpdate()
    {
        await fixture.ResetDataAsync();

        int productId;

        await using (var context = fixture.CreateContext())
        {
            var product = new AuditableProduct { Name = "Test Product", Price = 99.99m };
            context.Products.Add(product);
            await context.SaveChangesAsync();
            productId = product.Id;
        }

        await Task.Delay(100); // Ensure time passes

        await using (var context = fixture.CreateContext())
        {
            var product = await context.Products.FindAsync(productId);
            Assert.NotNull(product);

            var originalCreatedAt = product.CreatedAt;
            product.Price = 149.99m;
            await context.SaveChangesAsync();

            Assert.Equal(originalCreatedAt, product.CreatedAt);
            Assert.NotNull(product.LastModifiedAt);
            Assert.True(product.LastModifiedAt > product.CreatedAt);
        }
    }

    [Fact]
    public async Task SaveChanges_SetsLastModifiedBy_OnUpdate()
    {
        await fixture.ResetDataAsync();

        int productId;

        await using (var context = fixture.CreateContextWithUser("creator"))
        {
            var product = new AuditableProduct { Name = "Test Product", Price = 99.99m };
            context.Products.Add(product);
            await context.SaveChangesAsync();
            productId = product.Id;
        }

        await using (var context = fixture.CreateContextWithUser("modifier"))
        {
            var product = await context.Products.FindAsync(productId);
            Assert.NotNull(product);

            product.Price = 149.99m;
            await context.SaveChangesAsync();

            Assert.Equal("creator", product.CreatedBy);
            Assert.Equal("modifier", product.LastModifiedBy);
        }
    }

    public sealed class PostgresFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container;
        private string connectionString = string.Empty;

        public PostgresFixture()
        {
            container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("themia_audit_tests")
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

        public TestAuditDbContext CreateContext(string? userId = null) =>
            new(GetOptions(), userId);

        public TestAuditDbContext CreateContextWithUser(string userId) =>
            new(GetOptions(), userId);

        public async Task ResetDataAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
            await context.Products.ExecuteDeleteAsync();
        }

        private DbContextOptions<TestAuditDbContext> GetOptions()
        {
            return new DbContextOptionsBuilder<TestAuditDbContext>()
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

    public class TestAuditDbContext : ThemiaDbContext
    {
        private readonly string? userId;

        public TestAuditDbContext(DbContextOptions options, string? userId = null)
            : base(options, null, null)
        {
            this.userId = userId;
        }

        public DbSet<AuditableProduct> Products => Set<AuditableProduct>();

        protected override string? CurrentUserId => userId;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AuditableProduct>(entity =>
            {
                entity.ToTable("auditable_products");
                entity.HasKey(p => p.Id);
                entity.Property(p => p.Name).IsRequired().HasMaxLength(200);
                entity.Property(p => p.Price).HasPrecision(18, 2);
            });
            base.OnModelCreating(modelBuilder);
        }
    }

    public class AuditableProduct : IAuditableEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string? CreatedBy { get; set; }
        public DateTimeOffset? LastModifiedAt { get; set; }
        public string? LastModifiedBy { get; set; }
    }
}
