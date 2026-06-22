using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.Mapping;

/// <summary>Registers the Themia Notifications entity mappings (schema-qualified <c>notifications.*</c>
/// table names) into a Dapper <see cref="EntityMappingRegistry"/>, so the Dapper peer reads and writes
/// the exact same columns as the EF peer over the FluentMigrator-owned schema.</summary>
public static class NotificationsDapperMappings
{
    /// <summary>Registers the four Notifications entity mappings. Columns follow the snake_case
    /// convention, which matches the EF config and the migration one-for-one (<c>tenant_id</c>,
    /// <c>channel</c>/<c>status</c> as their integer enum values, <c>next_attempt_at</c>, etc.); only
    /// the table names are schema-qualified.</summary>
    /// <param name="registry">The registry to populate.</param>
    public static void Apply(EntityMappingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<OutboxMessage>(EntityMapping.ForConvention<OutboxMessage>("notifications.outbox_messages", null));
        registry.Register<InAppNotification>(EntityMapping.ForConvention<InAppNotification>("notifications.in_app_notifications", null));
        registry.Register<NotificationPreference>(EntityMapping.ForConvention<NotificationPreference>("notifications.notification_preferences", null));
        registry.Register<TenantProviderConfig>(EntityMapping.ForConvention<TenantProviderConfig>("notifications.tenant_provider_configs", null));
    }
}
