using Microsoft.Extensions.Logging;

namespace Themia.Notifications.Providers;

/// <summary>Development <see cref="ISmsSender"/> that logs instead of sending. Never contacts a server.</summary>
internal sealed class LoggerSmsSender(ILogger<LoggerSmsSender> logger) : ISmsSender
{
    public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogInformation("Themia.Notifications (logger sms): to {Recipient}", message.Recipient);
        return Task.FromResult(NotificationResult.Success());
    }
}
