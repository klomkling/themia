using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Data.EFCore.IntegrationTests.Tenancy;

/// <summary>
/// Integration coverage against a real PostgreSQL instance to validate tenant filters and soft delete behavior.
/// </summary>
[Trait("Category", "Integration")]
public class PostgresTenantIsolationTests : IClassFixture<PostgresTenantIsolationTests.PostgresFixture>
{
    private readonly PostgresFixture fixture;

    public PostgresTenantIsolationTests(PostgresFixture fixture)
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
        await SeedAsync();

        await using var context = fixture.CreateRuntimeContext(new TenantId("tenant-a"));

        var otherTenant = await context.Orders.FindAsync(2);

        Assert.Null(otherTenant);
    }

    [Fact]
    public async Task PerTenantModel_UsesIsolatedModelsPerTenant()
    {
        await fixture.ResetDataAsync();
        await SeedAsync();

        await using var contextA = fixture.CreatePerTenantModelContext(new TenantId("tenant-a"));
        await using var contextB = fixture.CreatePerTenantModelContext(new TenantId("tenant-b"));

        // Models should be compiled per-tenant when strategy is PerTenantModel
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
    public async Task RuntimeTenantAccess_FindMirrorsFilter_WhenStaticAccessorDifferFromInjected()
    {
        // Verifies that Find/FindAsync reads the SAME tenant source (TenantContextAccessor — the static
        // ambient accessor) as the runtime query filter. If they diverged, a context injected with
        // tenant-A but whose static accessor was subsequently overridden to tenant-B would allow
        // cross-tenant access via Find, contradicting what the filter enforces on LINQ queries.

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
        TenantContextAccessor.CurrentTenantId = new TenantId("tenant-b");

        // tenant-B row: static accessor = "tenant-b" → filter allows it → Find must return it.
        var foundB = await context.Orders.FindAsync(tenantBId);
        Assert.NotNull(foundB);
        Assert.Equal("MirrorTestB", foundB!.Name);

        // tenant-A row: static accessor = "tenant-b" → filter blocks it → Find must return null.
        var foundA = await context.Orders.FindAsync(tenantAId);
        Assert.Null(foundA);
    }

    private async Task SeedAsync()
    {
        await using var context = fixture.CreateRuntimeContext(null);
        context.Orders.Add(new TenantOrder { Name = "A", TenantId = new TenantId("tenant-a") });
        context.Orders.Add(new TenantOrder { Name = "B", TenantId = new TenantId("tenant-b") });
        context.Orders.Add(new TenantOrder { Name = "Global", TenantId = null });
        await context.SaveChangesAsync();
    }

    public sealed class PostgresFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container;
        private string connectionString = string.Empty;

        public PostgresFixture()
        {
            container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("themia_tests")
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

        public RuntimeTenantDbContext CreateRuntimeContext(TenantId? tenantId) =>
            new(GetOptions(), new TenantContext(tenantId));

        public PerTenantModelDbContext CreatePerTenantModelContext(TenantId? tenantId) =>
            new(GetOptions(), new TenantContext(tenantId));

        public async Task ResetDataAsync()
        {
            await using var context = CreateRuntimeContext(null);
            await context.Database.EnsureCreatedAsync();
            await context.Orders.IgnoreQueryFilters().ExecuteDeleteAsync();
        }

        private DbContextOptions<TestTenantDbContext> GetOptions()
        {
            return new DbContextOptionsBuilder<TestTenantDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options;
        }

        private async Task EnsureSchemaAsync()
        {
            await using var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS themia";
            await command.ExecuteNonQueryAsync();
        }
    }

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
