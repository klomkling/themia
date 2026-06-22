using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.EntityConfiguration;

internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.ToTable("notification_preferences", "notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        b.Property(x => x.UserId).HasColumnName("user_id").HasMaxLength(100);
        b.Property(x => x.Channel).HasColumnName("channel").HasConversion<int>();
        b.Property(x => x.IsEnabled).HasColumnName("is_enabled");
        b.Property(x => x.Locale).HasColumnName("locale").HasMaxLength(20);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(100);
        b.Property(x => x.LastModifiedAt).HasColumnName("last_modified_at");
        b.Property(x => x.LastModifiedBy).HasColumnName("last_modified_by").HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.UserId, x.Channel }).HasDatabaseName("ix_pref_tenant_user_channel");
    }
}
