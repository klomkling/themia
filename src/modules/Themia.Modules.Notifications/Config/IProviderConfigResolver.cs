using Themia.Modules.Notifications.Entities;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Config;

/// <summary>Resolves per-tenant provider credentials for a channel, or null to use the global config.</summary>
public interface IProviderConfigResolver
{
    /// <summary>Returns the current tenant's config for the channel, or null if none is set.</summary>
    /// <param name="channel">The channel whose provider configuration is requested.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>
    /// The per-tenant configuration, or null when none exists so the caller falls back to its
    /// globally-registered provider options.
    /// </returns>
    Task<TenantProviderConfig?> ResolveAsync(NotificationChannel channel, CancellationToken ct = default);
}
