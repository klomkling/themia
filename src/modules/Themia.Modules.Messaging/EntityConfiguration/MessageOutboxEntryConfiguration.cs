using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Messaging.Entities;

namespace Themia.Modules.Messaging.EntityConfiguration;

internal sealed class MessageOutboxEntryConfiguration : IEntityTypeConfiguration<MessageOutboxEntry>
{
    public void Configure(EntityTypeBuilder<MessageOutboxEntry> b)
    {
        b.ToTable("messaging_outbox_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.MessageId).HasColumnName("message_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id").HasMaxLength(100);
        b.Property(x => x.Type).HasColumnName("type").HasMaxLength(200).IsRequired();
        b.Property(x => x.Payload).HasColumnName("payload").IsRequired();
        b.Property(x => x.Destination).HasColumnName("destination").HasMaxLength(100).IsRequired();
        b.Property(x => x.Origin).HasColumnName("origin").HasMaxLength(100).IsRequired();
        b.Property(x => x.EntityKey).HasColumnName("entity_key").HasMaxLength(200);
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Headers).HasColumnName("headers");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
        b.Property(x => x.Attempts).HasColumnName("attempts");
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
        b.Property(x => x.ScheduledFor).HasColumnName("scheduled_for");
        b.Property(x => x.LeaseOwner).HasColumnName("lease_owner").HasMaxLength(100);
        b.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.SentAt).HasColumnName("sent_at");
        b.Property(x => x.LastError).HasColumnName("last_error");
        b.HasIndex(x => new { x.TenantId }).HasDatabaseName("ix_msg_outbox_tenant");
        b.HasIndex(x => new { x.MessageId, x.Destination }).HasDatabaseName("ux_msg_outbox_message_destination").IsUnique();
        b.HasIndex(x => new { x.Status, x.NextAttemptAt }).HasDatabaseName("ix_msg_outbox_claim");
        b.HasIndex(x => new { x.Status, x.SentAt }).HasDatabaseName("ix_msg_outbox_purge");
    }
}
