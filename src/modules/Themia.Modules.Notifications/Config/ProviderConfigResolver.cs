using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Stores;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Config;

/// <summary>
/// Resolves per-tenant provider configuration via <see cref="ITenantProviderConfigStore"/>,
/// returning null when no per-tenant row exists so the caller falls back to its global config.
/// </summary>
internal sealed class ProviderConfigResolver(ITenantProviderConfigStore store) : IProviderConfigResolver
{
    public async Task<TenantProviderConfig?> ResolveAsync(NotificationChannel channel, CancellationToken ct = default)
        => await store.FindAsync(channel, ct).ConfigureAwait(false);
}
