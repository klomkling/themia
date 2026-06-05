using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Tenancy;

public class TenantInfrastructureTests
{
    [Fact]
    public void Delete_WhenSoftDeleteFiltersDisabled_PerformsHardDelete()
    {
        var options = new DbContextOptionsBuilder<HardDeleteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var tenantContext = new TenantContext(new TenantId("tenant-a"));

        using (var context = new HardDeleteDbContext(options, tenantContext))
        {
            context.Orders.Add(new TestOrder { Id = 1, Name = "delete-me", TenantId = tenantContext.CurrentTenantId });
            context.SaveChanges();

            context.Remove(context.Orders.Single());
            context.SaveChanges();
        }

        using (var context = new HardDeleteDbContext(options, tenantContext))
        {
            var remaining = context.Orders.IgnoreQueryFilters().ToList();
            Assert.Empty(remaining);
        }
    }

    [Fact]
    public void SaveChanges_WithDefaultTenantId_DoesNotThrow()
    {
        var options = new DbContextOptionsBuilder<ComparerTestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var context = new ComparerTestDbContext(options, new TenantContext(null));

        var entity = new TenantItem
        {
            Id = 1,
            Name = "default-tenant",
            TenantId = new TenantId?((TenantId)default)
        };

        context.TenantItems.Add(entity);

        var exception = Record.Exception(() => context.SaveChanges());
        Assert.Null(exception);

        var stored = context.TenantItems.IgnoreQueryFilters().ToList();
        Assert.Single(stored);
        Assert.Null(stored[0].TenantId?.Value);
    }

    [Fact]
    public void ModelCache_IsNotSharedAcrossTenants()
    {
        // IMPORTANT: With the tenant filter fix, each tenant gets its own compiled model
        // This prevents the "freezing" bug where the first context's tenant would be
        // baked into the model and applied to all subsequent contexts
        var options = new DbContextOptionsBuilder<CacheTestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        using var contextA = new CacheTestDbContext(options, new TenantContext(new TenantId("tenant-a")));
        using var contextB = new CacheTestDbContext(options, new TenantContext(new TenantId("tenant-b")));

        // Different tenants should have different model instances
        Assert.NotSame(contextA.Model, contextB.Model);
    }

    private sealed class HardDeleteDbContext : ThemiaDbContext
    {
        public HardDeleteDbContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        protected override bool EnableSoftDeleteFilters => false;

        public DbSet<TestOrder> Orders => Set<TestOrder>();

        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.PerTenantModel;
    }

    private sealed class ComparerTestDbContext : ThemiaDbContext
    {
        public ComparerTestDbContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        public DbSet<TenantItem> TenantItems => Set<TenantItem>();

        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.PerTenantModel;
    }

    private sealed class CacheTestDbContext : ThemiaDbContext
    {
        public CacheTestDbContext(DbContextOptions options, ITenantContext? tenantContext)
            : base(options, tenantContext)
        {
        }

        public DbSet<CacheTenantItem> TenantItems => Set<CacheTenantItem>();

        protected override TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.PerTenantModel;
    }

    private sealed class TestOrder : ITenantEntity, ISoftDeletable
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

    private sealed class TenantItem : ITenantEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public TenantId? TenantId { get; set; }
    }

    private sealed class CacheTenantItem : ITenantEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public TenantId? TenantId { get; set; }
    }
}
