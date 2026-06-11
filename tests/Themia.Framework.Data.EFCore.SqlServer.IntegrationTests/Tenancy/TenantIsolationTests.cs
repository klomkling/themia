using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.Tenancy;

/// <summary>
/// Integration coverage against a real SQL Server instance to validate tenant filters and soft delete behavior.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerIntegrationCollection.Name)]
public class TenantIsolationTests : IClassFixture<TenantIsolationTests.SqlServerFixture>
{
    private readonly SqlServerFixture fixture;

    public TenantIsolationTests(SqlServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task RuntimeTenantAccess_FiltersTenantAndGlobal()
    {
        await fixture.ResetDataAsync();

        await SeedAsync();

        await using var context = fixture.CreateRuntimeContext(new TenantId("tenant-a"));

        var orders = await context.Orders.AsNoTracking().OrderBy(o => o.Id).ToListAsync();

        Assert.Equal(2, orders.Count);
        Assert.Contains(orders, o => o.Name == "A");
        Assert.Contains(orders, o => o.Name == "Global");
    }

    [Fact]
    public async Task RuntimeTenantAccess_BlocksFindAcrossTenants()
    {
        await fixture.ResetDataAsync();

        // Seed a tenant-b row and capture its id from the tracked entity — no extra lookup context,
        // which would churn the ambient tenant accessor. Mirrors RuntimeTenantAccess_FindMirrorsFilter.
        int tenantBId;
        await using (var seed = fixture.CreateRuntimeContext(null))
        {
            var orderB = new TenantOrder { Name = "B", TenantId = new TenantId("tenant-b") };
            seed.Orders.Add(orderB);
            await seed.SaveChangesAsync();
            tenantBId = orderB.Id;
        }

        // A tenant-a context must not see the tenant-b row by primary key — the DbSet.Find path included.
        // (Regression guard: the runtime tenant filter must be context-rooted so EF's pre-compiled
        // entity-finder query parameterizes the tenant instead of baking the first-seen value.)
        await using var context = fixture.CreateRuntimeContext(new TenantId("tenant-a"));

        var otherTenant = await context.Orders.FindAsync(tenantBId);

        Assert.Null(otherTenant);
    }

    [Fact]
    public async Task PerTenantModel_UsesIsolatedModelsPerTenant()
    {
        await fixture.ResetDataAsync();
        await SeedAsync();

        await using var contextA = fixture.CreatePerTenantModelContext(new TenantId("tenant-a"));
        await using var contextB = fixture.CreatePerTenantModelContext(new TenantId("tenant-b"));

        // Models should be compiled per-tenant when strategy is PerTenantModel.
        Assert.NotSame(contextA.Model, contextB.Model);

        var ordersA = await contextA.Orders.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
        var ordersB = await contextB.Orders.AsNoTracking().OrderBy(o => o.Id).ToListAsync();

        Assert.Equal(new[] { "A", "Global" }, ordersA.Select(o => o.Name).OrderBy(x => x));
        Assert.Equal(new[] { "B", "Global" }, ordersB.Select(o => o.Name).OrderBy(x => x));
    }

    [Fact]
    public async Task RuntimeTenantAccess_RespectsSoftDelete()
    {
        await fixture.ResetDataAsync();

        await using (var context = fixture.CreateRuntimeContext(new TenantId("tenant-a")))
        {
            context.Orders.Add(new TenantOrder { Name = "A", TenantId = new TenantId("tenant-a") });
            context.Orders.Add(new TenantOrder { Name = "Deleted", TenantId = new TenantId("tenant-a"), IsDeleted = true });
            await context.SaveChangesAsync();
        }

        await using var runtime = fixture.CreateRuntimeContext(new TenantId("tenant-a"));
        var orders = await runtime.Orders.ToListAsync();

        Assert.Single(orders);
        Assert.DoesNotContain(orders, o => o.IsDeleted);
    }

    [Fact]
    public async Task RuntimeTenantAccess_FindMirrorsFilter_WhenStaticAccessorDiffersFromInjectedContext()
    {
        await fixture.ResetDataAsync();

        int tenantAId;
        int tenantBId;

        // Seed one row per tenant using an unfiltered context.
        await using (var seed = fixture.CreateRuntimeContext(null))
        {
            var orderA = new TenantOrder { Name = "MirrorTestA", TenantId = new TenantId("tenant-a") };
            var orderB = new TenantOrder { Name = "MirrorTestB", TenantId = new TenantId("tenant-b") };
            seed.Orders.Add(orderA);
            seed.Orders.Add(orderB);
            await seed.SaveChangesAsync();
            tenantAId = orderA.Id;
            tenantBId = orderB.Id;
        }

        // Create a RuntimeTenantAccess context injected with tenant-A.
        // The ctor sets TenantContextAccessor.CurrentTenantId = "tenant-a".
        await using var context = fixture.CreateRuntimeContext(new TenantId("tenant-a"));

        // Now simulate divergence: override the static accessor to tenant-B AFTER construction.
        // The injected ITenantContext still says "tenant-a", but the filter source says "tenant-b".
        // Find must follow the static accessor (the filter's actual source), not the injected context.
        // Save and restore so this manual override does not leak into subsequent tests.
        var savedTenantId = TenantContextAccessor.CurrentTenantId;
        TenantContextAccessor.CurrentTenantId = new TenantId("tenant-b");
        try
        {
            // tenant-B row: static accessor = "tenant-b" → filter allows it → Find must return it.
            var foundB = await context.Orders.FindAsync(tenantBId);
            Assert.NotNull(foundB);
            Assert.Equal("MirrorTestB", foundB!.Name);

            // tenant-A row: static accessor = "tenant-b" → filter blocks it → Find must return null.
            var foundA = await context.Orders.FindAsync(tenantAId);
            Assert.Null(foundA);
        }
        finally
        {
            TenantContextAccessor.CurrentTenantId = savedTenantId;
        }
    }

    private async Task SeedAsync()
    {
        await using var context = fixture.CreateRuntimeContext(null);
        context.Orders.Add(new TenantOrder { Name = "A", TenantId = new TenantId("tenant-a") });
        context.Orders.Add(new TenantOrder { Name = "B", TenantId = new TenantId("tenant-b") });
        context.Orders.Add(new TenantOrder { Name = "Global", TenantId = null });
        await context.SaveChangesAsync();
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
            connectionString = sharedContainer.GetConnectionString("ef_tenancy");
            await using var context = CreateRuntimeContext(null);
            await context.Database.EnsureCreatedAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        public RuntimeTenantDbContext CreateRuntimeContext(TenantId? tenantId) =>
            new(GetOptions(), new TenantContext(tenantId));

        public PerTenantModelDbContext CreatePerTenantModelContext(TenantId? tenantId) =>
            new(GetOptions(), new TenantContext(tenantId));

        public async Task ResetDataAsync()
        {
            await using var context = CreateRuntimeContext(null);
            await context.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
        }

        private DbContextOptions<TestTenantDbContext> GetOptions() =>
            new DbContextOptionsBuilder<TestTenantDbContext>()
                .UseSqlServer(connectionString)
                .Options;
    }

    // ── Test context hierarchy ────────────────────────────────────────────────

    public abstract class TestTenantDbContext : ThemiaDbContext
    {
        protected TestTenantDbContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        public DbSet<TenantOrder> Orders => Set<TenantOrder>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TenantOrder>(builder =>
            {
                builder.ToTable("tenant_orders", schema: "themia");
                builder.HasKey(o => o.Id);
                builder.Property(o => o.Name).IsRequired();
                builder.Property(o => o.TenantId);
            });
            base.OnModelCreating(modelBuilder);
        }
    }

    public sealed class RuntimeTenantDbContext : TestTenantDbContext
    {
        public RuntimeTenantDbContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.RuntimeTenantAccess;
    }

    public sealed class PerTenantModelDbContext : TestTenantDbContext
    {
        public PerTenantModelDbContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.PerTenantModel;
    }

    // ── Entity ────────────────────────────────────────────────────────────────

    public sealed class TenantOrder : ITenantEntity, ISoftDeletable
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public TenantId? TenantId { get; set; }
        public bool IsDeleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? RestoredAt { get; set; }
        public string? RestoredBy { get; set; }
    }
}
