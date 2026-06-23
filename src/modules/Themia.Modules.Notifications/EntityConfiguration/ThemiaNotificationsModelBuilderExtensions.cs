using Microsoft.EntityFrameworkCore;

namespace Themia.Modules.Notifications.EntityConfiguration;

/// <summary>Applies the Notifications module's EF Core entity configurations.</summary>
public static class ThemiaNotificationsModelBuilderExtensions
{
    /// <summary>
    /// Registers the outbox, in-app, preference, and provider-config entities on the given model.
    /// Call from your <c>ThemiaDbContext.OnModelCreating</c>; the base context applies tenant and
    /// soft-delete query filters to entities implementing the framework marker interfaces.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyThemiaNotifications(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InAppNotificationConfiguration());
        modelBuilder.ApplyConfiguration(new NotificationPreferenceConfiguration());
        modelBuilder.ApplyConfiguration(new TenantProviderConfigConfiguration());
        return modelBuilder;
    }
}
