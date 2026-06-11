using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.Audit;

/// <summary>
/// Integration tests for audit tracking functionality with real SQL Server.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerIntegrationCollection.Name)]
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

        var clock = new TestTimeProvider();
        var beforeCreate = clock.Now;

        await using (var context = fixture.CreateContext(clock: clock))
        {
            var product = new AuditableProduct { Name = "Test Product", Price = 99.99m };
            context.Products.Add(product);
            await context.SaveChangesAsync();

            Assert.True(product.CreatedAt >= beforeCreate);
            Assert.True(product.CreatedAt <= clock.Now);
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

        var clock = new TestTimeProvider();
        int productId;

        await using (var context = fixture.CreateContext(clock: clock))
        {
            var product = new AuditableProduct { Name = "Test Product", Price = 99.99m };
            context.Products.Add(product);
            await context.SaveChangesAsync();
            productId = product.Id;
        }

        clock.Now = clock.Now.AddMinutes(1);

        await using (var context = fixture.CreateContext(clock: clock))
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

        var clock = new TestTimeProvider();
        int productId;
        DateTimeOffset originalCreatedAt;

        await using (var context = fixture.CreateContext(clock: clock))
        {
            var product = new AuditableProduct { Name = "Test Product", Price = 99.99m };
            context.Products.Add(product);
            await context.SaveChangesAsync();
            productId = product.Id;
            originalCreatedAt = product.CreatedAt;
        }

        clock.Now = clock.Now.AddMinutes(1);

        await using (var context = fixture.CreateContext(clock: clock))
        {
            var product = await context.Products.FindAsync(productId);
            Assert.NotNull(product);
            product.Price = 149.99m;
            await context.SaveChangesAsync();
        }

        clock.Now = clock.Now.AddMinutes(1);

        await using (var context = fixture.CreateContext(clock: clock))
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
        private readonly SharedSqlServerContainerFixture sharedContainer;
        private string connectionString = string.Empty;

        public SqlServerFixture(SharedSqlServerContainerFixture sharedContainer)
        {
            this.sharedContainer = sharedContainer;
        }

        public async Task InitializeAsync()
        {
            connectionString = sharedContainer.GetConnectionString("ef_audit");
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        public TestAuditDbContext CreateContext(string? userId = null, TimeProvider? clock = null) =>
            new(GetOptions(), userId, clock);

        public TestAuditDbContext CreateContextWithUser(string userId) =>
            new(GetOptions(), userId);

        public async Task ResetDataAsync()
        {
            await using var context = CreateContext();
            await context.Products.ExecuteDeleteAsync();
        }

        private DbContextOptions<TestAuditDbContext> GetOptions() =>
            new DbContextOptionsBuilder<TestAuditDbContext>()
                .UseSqlServer(connectionString)
                .Options;
    }

    // ── Test context ──────────────────────────────────────────────────────────

    public class TestAuditDbContext : ThemiaDbContext
    {
        private readonly string? userId;

        public TestAuditDbContext(DbContextOptions options, string? userId = null, TimeProvider? timeProvider = null)
            : base(options, null, timeProvider)
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

    // ── Time provider ─────────────────────────────────────────────────────────

    private sealed class TestTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
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
