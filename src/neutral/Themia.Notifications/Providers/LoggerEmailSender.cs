using Microsoft.Extensions.Logging;

namespace Themia.Notifications.Providers;

/// <summary>Development <see cref="IEmailSender"/> that logs instead of sending. Never contacts a server.</summary>
internal sealed class LoggerEmailSender(ILogger<LoggerEmailSender> logger) : IEmailSender
{
    public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogInformation("Themia.Notifications (logger email): to {Recipient} subject {Subject}", message.Recipient, message.Subject);
        return Task.FromResult(NotificationResult.Success());
    }
}
