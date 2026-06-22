using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Stores;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Dispatch;

/// <summary>
/// Resolves enabled channels and locale from stored preferences using an opt-out model:
/// a channel with no row is enabled, and a per-user row overrides the tenant-wide default.
/// </summary>
internal sealed class PreferenceResolver(INotificationPreferenceStore store) : IPreferenceResolver
{
    public async Task<ResolvedPreferences> ResolveAsync(
        string userId, IReadOnlyList<NotificationChannel> requested, CancellationToken ct = default)
    {
        // The store returns the user's rows plus the tenant-wide defaults (null UserId).
        var preferences = await store.ListAsync(userId, ct).ConfigureAwait(false);

        var enabled = requested
            .Where(channel => IsEnabled(channel, userId, preferences))
            .ToList();

        return new ResolvedPreferences(enabled, ResolveLocale(userId, preferences));
    }

    // Opt-out: absence of any row leaves the channel enabled; a user row wins over the tenant default.
    private static bool IsEnabled(
        NotificationChannel channel, string userId, IReadOnlyList<NotificationPreference> preferences)
    {
        var row = FindWinningRow(preferences, p => p.Channel == channel, userId);
        return row?.IsEnabled ?? true;
    }

    // The user's locale wins; otherwise fall back to the tenant default; otherwise the app default (null).
    private static string? ResolveLocale(string userId, IReadOnlyList<NotificationPreference> preferences)
    {
        var row = FindWinningRow(preferences, p => p.Locale is not null, userId);
        return row?.Locale;
    }

    // Prefer the matching user row over the matching tenant-default (null UserId) row.
    private static NotificationPreference? FindWinningRow(
        IReadOnlyList<NotificationPreference> preferences,
        Func<NotificationPreference, bool> match,
        string userId)
    {
        NotificationPreference? userRow = null;
        NotificationPreference? tenantRow = null;
        foreach (var preference in preferences)
        {
            if (!match(preference))
                continue;
            if (preference.UserId == userId)
                userRow = preference;
            else if (preference.UserId is null)
                tenantRow = preference;
        }

        return userRow ?? tenantRow;
    }
}
