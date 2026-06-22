namespace Themia.Notifications;

/// <summary>Sends a mobile/web push notification via a configured provider. Provider seam — no
/// built-in provider ships in v1; hosts supply a concrete implementation.</summary>
public interface IPushSender
{
    /// <summary>Sends <paramref name="message"/>. Throws on provider failure (callers/drainer own retry).</summary>
    Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}
