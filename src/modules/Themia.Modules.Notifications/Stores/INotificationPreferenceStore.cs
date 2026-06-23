using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.Stores;

/// <summary>Reads and writes notification preferences for the current tenant.</summary>
public interface INotificationPreferenceStore
{
    /// <summary>
    /// Returns the preferences for a user (and the tenant-wide defaults), or all preferences
    /// in the tenant when <paramref name="userId"/> is null.
    /// </summary>
    /// <param name="userId">The user to filter by, or null for every preference in the tenant.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The matching preferences.</returns>
    Task<IReadOnlyList<NotificationPreference>> ListAsync(string? userId, CancellationToken ct = default);

    /// <summary>Inserts a new preference or updates the existing one for the same (user, channel).</summary>
    /// <param name="preference">The preference to persist.</param>
    /// <param name="ct">A cancellation token.</param>
    Task UpsertAsync(NotificationPreference preference, CancellationToken ct = default);
}
