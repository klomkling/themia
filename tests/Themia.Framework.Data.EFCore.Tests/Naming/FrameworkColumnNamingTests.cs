using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Naming;

public sealed class FrameworkColumnNamingTests
{
    // A framework entity that exercises every marker: key (Entity<int>), audit + soft-delete
    // (SoftDeletableEntity), tenant (ITenantEntity), concurrency (IConcurrencyAware), plus one
    // adopter-owned column (AppName) that Themia must NOT rename.
    private sealed class Probe : SoftDeletableEntity<int>, ITenantEntity, IConcurrencyAware
    {
        public TenantId? TenantId { get; set; }
        public byte[]? RowVersion { get; set; }
        public string AppName { get; set; } = string.Empty;
    }

    private sealed class ProbeContext(DbContextOptions options) : ThemiaDbContext(options)
    {
        public DbSet<Probe> Probes => Set<Probe>();
    }

    private static ProbeContext NewContext()
    {
        // SQLite gives a real relational model for column-name introspection, no global naming convention.
        var options = new DbContextOptionsBuilder<ProbeContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new ProbeContext(options);
    }

    private static string? ColumnOf(ProbeContext ctx, string property)
    {
        var entityType = ctx.Model.FindEntityType(typeof(Probe))!;
        var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)!.Value;
        var prop = entityType.FindProperty(property);
        Assert.NotNull(prop);
        return prop.GetColumnName(store);
    }

    [Fact]
    public void FrameworkColumns_AreSnakeCase_WithoutGlobalConvention()
    {
        using var ctx = NewContext();

        Assert.Equal("id", ColumnOf(ctx, "Id"));
        Assert.Equal("tenant_id", ColumnOf(ctx, nameof(Probe.TenantId)));
        Assert.Equal("created_at", ColumnOf(ctx, "CreatedAt"));
        Assert.Equal("created_by", ColumnOf(ctx, "CreatedBy"));
        Assert.Equal("last_modified_at", ColumnOf(ctx, "LastModifiedAt"));
        Assert.Equal("last_modified_by", ColumnOf(ctx, "LastModifiedBy"));
        Assert.Equal("is_deleted", ColumnOf(ctx, "IsDeleted"));
        Assert.Equal("deleted_at", ColumnOf(ctx, "DeletedAt"));
        Assert.Equal("deleted_by", ColumnOf(ctx, "DeletedBy"));
        Assert.Equal("restored_at", ColumnOf(ctx, "RestoredAt"));
        Assert.Equal("restored_by", ColumnOf(ctx, "RestoredBy"));
        Assert.Equal("row_version", ColumnOf(ctx, nameof(Probe.RowVersion)));
    }

    [Fact]
    public void AdopterColumns_AreUntouched_WithoutGlobalConvention()
    {
        using var ctx = NewContext();

        // No global convention applied here, so the adopter's own column keeps its PascalCase name.
        Assert.Equal("AppName", ColumnOf(ctx, nameof(Probe.AppName)));
    }
}
