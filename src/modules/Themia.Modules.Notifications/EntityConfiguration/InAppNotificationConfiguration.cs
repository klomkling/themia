using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.EntityConfiguration;

internal sealed class InAppNotificationConfiguration : IEntityTypeConfiguration<InAppNotification>
{
    public void Configure(EntityTypeBuilder<InAppNotification> b)
    {
        b.ToTable("in_app_notifications", "notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(100).IsRequired();
        b.Property(x => x.Title).HasColumnName("title").HasMaxLength(512).IsRequired();
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.Property(x => x.IsRead).HasColumnName("is_read");
        b.Property(x => x.ReadAt).HasColumnName("read_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(100);
        b.Property(x => x.LastModifiedAt).HasColumnName("last_modified_at");
        b.Property(x => x.LastModifiedBy).HasColumnName("last_modified_by").HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.UserId, x.IsRead }).HasDatabaseName("ix_in_app_tenant_user");
    }
}
