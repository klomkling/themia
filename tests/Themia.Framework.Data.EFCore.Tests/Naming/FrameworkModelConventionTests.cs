using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Naming;

/// <summary>
/// Model-convention edge cases without a global naming convention (SQLite model): TenantId
/// reference-column conversion for adopter shapes, owned-type skipping, and adopter key naming.
/// </summary>
public sealed class FrameworkModelConventionTests
{
    [Fact]
    public void TenantIdReferenceColumns_GetValueConverters()
    {
        // Regression guard: adopter TenantId-typed REFERENCE columns — nullable or not, on tenant-scoped
        // or unscoped entities — must receive the value converter even though they are not the
        // ITenantEntity.TenantId property.
        using var ctx = NewContext();

        var link = ctx.Model.FindEntityType(typeof(TenantLink))!;
        Assert.NotNull(link.FindProperty(nameof(TenantLink.OwnerTenant))!.GetValueConverter());
        Assert.NotNull(link.FindProperty(nameof(TenantLink.MaybeTenant))!.GetValueConverter());

        var doc = ctx.Model.FindEntityType(typeof(ScopedDoc))!;
        Assert.NotNull(doc.FindProperty(nameof(ScopedDoc.TenantId))!.GetValueConverter());
        Assert.NotNull(doc.FindProperty(nameof(ScopedDoc.RelatedTenant))!.GetValueConverter());
    }

    [Fact]
    public void OwnedTypes_AreSkipped_AndKeepTheirColumnNames()
    {
        using var ctx = NewContext();

        var owned = ctx.Model.FindEntityType(typeof(Address))!;
        var store = StoreObjectIdentifier.Create(owned, StoreObjectType.Table)!.Value;

        // Owned-type columns are untouched by the framework mapping — and the model builds at all,
        // proving ApplyFrameworkColumnNames skips owned types (modelBuilder.Entity() would throw).
        Assert.Equal("Home_Street", owned.FindProperty(nameof(Address.Street))!.GetColumnName(store));
    }

    [Fact]
    public void AdopterKey_NotEntityDerived_KeepsItsName()
    {
        using var ctx = NewContext();

        var link = ctx.Model.FindEntityType(typeof(TenantLink))!;
        var store = StoreObjectIdentifier.Create(link, StoreObjectType.Table)!.Value;

        // 'Id' maps to "id" only for Entity<>-derived types; an adopter-declared key keeps EF's default.
        Assert.Equal("Id", link.FindProperty(nameof(TenantLink.Id))!.GetColumnName(store));
    }

    private static ConventionContext NewContext() =>
        new(new DbContextOptionsBuilder<ConventionContext>().UseSqlite("Data Source=:memory:").Options);

    // Non-tenant entity carrying TenantId-typed REFERENCE columns (e.g. an audit-log row pointing at a
    // tenant). Does not derive Entity<> and implements no framework marker.
    private sealed class TenantLink
    {
        public int Id { get; set; }
        public TenantId OwnerTenant { get; set; }
        public TenantId? MaybeTenant { get; set; }
    }

    // Tenant-scoped entity with an EXTRA TenantId reference besides the interface property.
    private sealed class ScopedDoc : ITenantEntity
    {
        public int Id { get; set; }
        public TenantId? TenantId { get; set; }
        public TenantId? RelatedTenant { get; set; }
    }

    private sealed class Owner
    {
        public int Id { get; set; }
        public Address Home { get; set; } = new();
    }

    private sealed class Address
    {
        public string Street { get; set; } = string.Empty;
    }

    private sealed class ConventionContext(DbContextOptions options) : ThemiaDbContext(options)
    {
        public DbSet<TenantLink> Links => Set<TenantLink>();
        public DbSet<ScopedDoc> Docs => Set<ScopedDoc>();
        public DbSet<Owner> Owners => Set<Owner>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Owner>().OwnsOne(o => o.Home);
            base.OnModelCreating(modelBuilder);
        }
    }
}
