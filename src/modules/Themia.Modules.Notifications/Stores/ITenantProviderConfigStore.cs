using Themia.Modules.Notifications.Entities;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Stores;

/// <summary>Reads and writes per-tenant provider configuration for the current tenant.</summary>
public interface ITenantProviderConfigStore
{
    /// <summary>Returns the provider configuration for a channel in the current tenant, or null.</summary>
    /// <param name="channel">The channel whose configuration is requested.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The configuration, or null when none is set for the current tenant and channel.</returns>
    Task<TenantProviderConfig?> FindAsync(NotificationChannel channel, CancellationToken ct = default);

    /// <summary>Inserts a new configuration or updates the existing one for the same channel.</summary>
    /// <param name="config">The configuration to persist.</param>
    /// <param name="ct">A cancellation token.</param>
    Task UpsertAsync(TenantProviderConfig config, CancellationToken ct = default);
}
