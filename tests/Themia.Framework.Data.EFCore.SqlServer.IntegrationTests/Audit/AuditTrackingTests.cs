using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.Audit;

/// <summary>
/// Integration tests for audit tracking functionality with real SQL Server.
/// </summary>
[Trait("Category", "Integration")]
public class AuditTrackingTests : IClassFixture<AuditTrackingTests.SqlServerFixture>
{
    private readonly SqlServerFixture fixture;

    public AuditTrackingTests(SqlServerFixture fixture)
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

        await using (var context = fixture.CreateContextWithUser("updater"))
        {
            var product = await context.Products.FindAsync(productId);
            Assert.NotNull(product);

            product.Price = 149.99m;
            await context.SaveChangesAsync();

            Assert.Equal("creator", product.CreatedBy);
            Assert.Equal("updater", product.LastModifiedBy);
        }
    }

    [Fact]
    public async Task SaveChanges_PreservesCreatedAt_OnSubsequentUpdates()
    {
        await fixture.ResetDataAsync();

        int productId;
        DateTimeOffset originalCreatedAt;

        await using (var context = fixture.CreateContext())
        {
            var product = new AuditableProduct { Name = "Test Product", Price = 99.99m };
            context.Products.Add(product);
            await context.SaveChangesAsync();
            productId = product.Id;
            originalCreatedAt = product.CreatedAt;
        }

        await Task.Delay(50);

        await using (var context = fixture.CreateContext())
        {
            var product = await context.Products.FindAsync(productId);
            Assert.NotNull(product);
            product.Price = 149.99m;
            await context.SaveChangesAsync();
        }

        await Task.Delay(50);

        await using (var context = fixture.CreateContext())
        {
            var product = await context.Products.FindAsync(productId);
            Assert.NotNull(product);
            product.Price = 199.99m;
            await context.SaveChangesAsync();

            Assert.Equal(originalCreatedAt, product.CreatedAt);
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

        private DbContextOptions<TestAuditDbContext> GetOptions() =>
            new DbContextOptionsBuilder<TestAuditDbContext>()
                .UseSqlServer(connectionString)
                .Options;

        private async Task EnsureSchemaAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }
    }

    // ── Test context ──────────────────────────────────────────────────────────

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

    // ── Entity ────────────────────────────────────────────────────────────────

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
