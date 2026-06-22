using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.EntityConfiguration;

internal sealed class TenantProviderConfigConfiguration : IEntityTypeConfiguration<TenantProviderConfig>
{
    public void Configure(EntityTypeBuilder<TenantProviderConfig> b)
    {
        b.ToTable("tenant_provider_configs", "notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        b.Property(x => x.Channel).HasColumnName("channel").HasConversion<int>();
        b.Property(x => x.Host).HasColumnName("host").HasMaxLength(256);
        b.Property(x => x.Port).HasColumnName("port");
        b.Property(x => x.Username).HasColumnName("username").HasMaxLength(256);
        b.Property(x => x.Password).HasColumnName("password").HasMaxLength(512);
        b.Property(x => x.FromAddress).HasColumnName("from_address").HasMaxLength(256);
        b.Property(x => x.UseSsl).HasColumnName("use_ssl");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by").HasMaxLength(100);
        b.Property(x => x.LastModifiedAt).HasColumnName("last_modified_at");
        b.Property(x => x.LastModifiedBy).HasColumnName("last_modified_by").HasMaxLength(100);
        b.HasIndex(x => new { x.TenantId, x.Channel }).HasDatabaseName("ix_provider_tenant_channel");
    }
}
