using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Xunit;

namespace Themia.Framework.Data.EFCore.IntegrationTests.Database;

/// <summary>
/// Integration tests verifying that <see cref="IDataFilterScope.BypassSoftDeleteFilter"/> causes
/// EF query filters to include soft-deleted rows while keeping tenant isolation enforced.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SoftDeleteBypassTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task BypassSoftDeleteFilter_includes_deleted_but_keeps_tenant_isolation()
    {
        await fixture.ResetDataAsync();
        var scope = new DataFilterScope();

        // Tenant A: one live + one soft-deleted product.
        await using (var ctx = fixture.CreateContext(new TenantId("a")))
        {
            ctx.Products.Add(new MigrationProduct { Id = Guid.NewGuid(), Name = "live", Sku = "L1", TenantId = new TenantId("a") });
            var gone = new MigrationProduct { Id = Guid.NewGuid(), Name = "gone", Sku = "G1", TenantId = new TenantId("a"), IsDeleted = true };
            ctx.Products.Add(gone);
            await ctx.SaveChangesAsync();
        }

        // Tenant B: one live product that must stay invisible to A.
        await using (var ctx = fixture.CreateContext(new TenantId("b")))
        {
            ctx.Products.Add(new MigrationProduct { Id = Guid.NewGuid(), Name = "b-live", Sku = "B1", TenantId = new TenantId("b") });
            await ctx.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext(new TenantId("a"), scope);
        using (scope.BypassSoftDeleteFilter())
        {
            var rows = await read.Products.ToListAsync();
            Assert.Equal(2, rows.Count);                                                    // A's live + A's soft-deleted
            Assert.All(rows, p => Assert.Equal(new TenantId("a"), p.TenantId));            // never B's row
        }
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container;
    private string connectionString = string.Empty;

    public PostgresFixture()
    {
        container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("themia_sd_bypass_tests")
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

    public TestMigrationDbContext CreateContext(TenantId? tenantId = null, IDataFilterScope? scope = null) =>
        new(GetOptions(), tenantId != null ? new TenantContext(tenantId) : null);

    public async Task ResetDataAsync()
    {
        await using var context = CreateContext();
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
    }

    private DbContextOptions<TestMigrationDbContext> GetOptions() =>
        new DbContextOptionsBuilder<TestMigrationDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
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
            entity.ToTable("sd_bypass_products");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Name).IsRequired().HasMaxLength(200);
            entity.Property(p => p.Sku).IsRequired().HasMaxLength(50);
            entity.Property(p => p.TenantId);
        });
        base.OnModelCreating(modelBuilder);
    }
}

public sealed class MigrationProduct : ITenantEntity, ISoftDeletable
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public TenantId? TenantId { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletedBy { get; set; }
    public DateTimeOffset? RestoredAt { get; set; }
    public string? RestoredBy { get; set; }
}
