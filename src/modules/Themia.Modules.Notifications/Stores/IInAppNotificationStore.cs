using Themia.Modules.Notifications.Entities;

namespace Themia.Modules.Notifications.Stores;

/// <summary>Reads and writes in-app notifications for the current tenant.</summary>
public interface IInAppNotificationStore
{
    /// <summary>Persists a new in-app notification.</summary>
    /// <param name="notification">The notification to persist.</param>
    /// <param name="ct">A cancellation token.</param>
    Task AddAsync(InAppNotification notification, CancellationToken ct = default);

    /// <summary>Returns a user's notifications, newest first.</summary>
    /// <param name="userId">The recipient user identifier.</param>
    /// <param name="unreadOnly">When true, returns only unread notifications.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The matching notifications, newest first.</returns>
    Task<IReadOnlyList<InAppNotification>> ListForUserAsync(string userId, bool unreadOnly, CancellationToken ct = default);

    /// <summary>Marks a notification read; returns false if not found for the current tenant.</summary>
    /// <param name="id">The notification identifier.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True if a notification was marked read; false if none matched in the current tenant.</returns>
    Task<bool> MarkReadAsync(Guid id, CancellationToken ct = default);
}
