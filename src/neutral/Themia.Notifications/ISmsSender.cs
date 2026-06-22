namespace Themia.Notifications;

/// <summary>Sends an SMS / text-message notification via a configured provider.</summary>
public interface ISmsSender
{
    /// <summary>Sends <paramref name="message"/>. Throws on provider failure (callers/drainer own retry).</summary>
    Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}
