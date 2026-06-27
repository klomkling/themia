using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.EntityConfiguration;

internal sealed class ExportRunConfiguration : IEntityTypeConfiguration<ExportRun>
{
    public void Configure(EntityTypeBuilder<ExportRun> b)
    {
        b.ToTable("export_runs", "export");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(100);
        b.Property(x => x.UserId).HasMaxLength(100);
        b.Property(x => x.DefinitionKey).IsRequired().HasMaxLength(200);
        b.Property(x => x.Format).HasConversion<int>();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.StorageKey).HasMaxLength(400);
        b.Property(x => x.FileName).HasMaxLength(260);
        b.HasIndex(x => new { x.TenantId, x.Status }).HasDatabaseName("ix_export_runs_tenant_status");
        b.HasIndex(x => x.ExpiresAt).HasDatabaseName("ix_export_runs_expires_at");
    }
}
