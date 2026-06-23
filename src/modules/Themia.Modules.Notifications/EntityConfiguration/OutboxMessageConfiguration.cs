using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.EntityConfiguration;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages", "notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        b.Property(x => x.Channel).HasColumnName("channel").HasConversion<int>();
        b.Property(x => x.Recipient).HasColumnName("recipient").HasMaxLength(512).IsRequired();
        b.Property(x => x.Subject).HasColumnName("subject").HasMaxLength(1024);
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
        b.Property(x => x.Attempts).HasColumnName("attempts");
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
        b.Property(x => x.ScheduledFor).HasColumnName("scheduled_for");
        b.Property(x => x.LeaseOwner).HasColumnName("lease_owner").HasMaxLength(100);
        b.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.SentAt).HasColumnName("sent_at");
        b.Property(x => x.LastError).HasColumnName("last_error");
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_outbox_claim");
    }
}
