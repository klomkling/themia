namespace Themia.Notifications;

/// <summary>Sends an email notification via a configured provider.</summary>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="message"/>. Throws on provider failure (callers/drainer own retry).</summary>
    Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}
