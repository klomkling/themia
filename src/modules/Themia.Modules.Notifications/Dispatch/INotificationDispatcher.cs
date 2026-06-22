namespace Themia.Modules.Notifications.Dispatch;

/// <summary>
/// Routes a notification request to its channels: external channels are enqueued on the outbox
/// (delivered by the drainer); in-app is written directly. Staged in the caller's unit of work —
/// commit (or a mediator transaction behavior) persists it atomically with the triggering work.
/// </summary>
public interface INotificationDispatcher
{
    /// <summary>Dispatches the request after applying recipient preferences.</summary>
    /// <param name="request">The notification request to route.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A task that completes once all channels have been staged.</returns>
    Task DispatchAsync(NotificationRequest request, CancellationToken ct = default);
}
