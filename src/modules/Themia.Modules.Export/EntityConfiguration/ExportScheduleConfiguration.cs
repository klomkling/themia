using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Export.Entities;

namespace Themia.Modules.Export.EntityConfiguration;

internal sealed class ExportScheduleConfiguration : IEntityTypeConfiguration<ExportSchedule>
{
    public void Configure(EntityTypeBuilder<ExportSchedule> b)
    {
        b.ToTable("export_schedules", "export");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(100);
        b.Property(x => x.UserId).HasMaxLength(100);
        b.Property(x => x.DefinitionKey).IsRequired().HasMaxLength(200);
        b.Property(x => x.Format).HasConversion<int>();
        b.Property(x => x.Cron).IsRequired().HasMaxLength(120);
        b.HasIndex(x => new { x.TenantId, x.Enabled }).HasDatabaseName("ix_export_schedules_tenant_enabled");
    }
}
