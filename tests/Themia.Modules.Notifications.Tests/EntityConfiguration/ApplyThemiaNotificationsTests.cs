using Microsoft.EntityFrameworkCore;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.EntityConfiguration;
using Xunit;

namespace Themia.Modules.Notifications.Tests.EntityConfiguration;

public class ApplyThemiaNotificationsTests
{
    private sealed class ProbeContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.ApplyThemiaNotifications();
    }

    private static ProbeContext CreateContext()
    {
        var options = new DbContextOptionsBuilder().UseInMemoryDatabase("probe").Options;
        return new ProbeContext(options);
    }

    [Fact]
    public void Maps_outbox_message_to_notifications_schema()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(OutboxMessage));
        Assert.NotNull(entity);
        Assert.Equal("outbox_messages", entity!.GetTableName());
        Assert.Equal("notifications", entity.GetSchema());
        Assert.Equal("tenant_id", entity.FindProperty(nameof(OutboxMessage.TenantId))!.GetColumnName());
        Assert.Equal("next_attempt_at", entity.FindProperty(nameof(OutboxMessage.NextAttemptAt))!.GetColumnName());
        Assert.Equal("lease_owner", entity.FindProperty(nameof(OutboxMessage.LeaseOwner))!.GetColumnName());
    }

    [Fact]
    public void Maps_in_app_notification_to_notifications_schema()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(InAppNotification));
        Assert.NotNull(entity);
        Assert.Equal("in_app_notifications", entity!.GetTableName());
        Assert.Equal("notifications", entity.GetSchema());
        Assert.Equal("is_read", entity.FindProperty(nameof(InAppNotification.IsRead))!.GetColumnName());
        Assert.Equal("read_at", entity.FindProperty(nameof(InAppNotification.ReadAt))!.GetColumnName());
        Assert.Equal("created_at", entity.FindProperty(nameof(InAppNotification.CreatedAt))!.GetColumnName());
        Assert.Equal("last_modified_by", entity.FindProperty(nameof(InAppNotification.LastModifiedBy))!.GetColumnName());
    }

    [Fact]
    public void Maps_notification_preference_to_notifications_schema()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(NotificationPreference));
        Assert.NotNull(entity);
        Assert.Equal("notification_preferences", entity!.GetTableName());
        Assert.Equal("notifications", entity.GetSchema());
        Assert.Equal("is_enabled", entity.FindProperty(nameof(NotificationPreference.IsEnabled))!.GetColumnName());
        Assert.Equal("locale", entity.FindProperty(nameof(NotificationPreference.Locale))!.GetColumnName());
    }

    [Fact]
    public void Maps_tenant_provider_config_to_notifications_schema()
    {
        using var ctx = CreateContext();
        var entity = ctx.Model.FindEntityType(typeof(TenantProviderConfig));
        Assert.NotNull(entity);
        Assert.Equal("tenant_provider_configs", entity!.GetTableName());
        Assert.Equal("notifications", entity.GetSchema());
        Assert.Equal("from_address", entity.FindProperty(nameof(TenantProviderConfig.FromAddress))!.GetColumnName());
        Assert.Equal("use_ssl", entity.FindProperty(nameof(TenantProviderConfig.UseSsl))!.GetColumnName());
    }
}
