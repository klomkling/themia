using Microsoft.Extensions.Logging;

namespace Themia.Notifications.Providers;

/// <summary>
/// Development <see cref="ISmsSender"/> that logs instead of sending. Never contacts a server, and
/// reports <see cref="NotificationResult.NoProviderConfigured"/> so a caller is never told a message was
/// delivered when none was.
/// </summary>
internal sealed class LoggerSmsSender(ILogger<LoggerSmsSender> logger) : ISmsSender
{
    public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Warning, not Information: this line is the only signal that a host expecting real delivery is
        // getting none. Information-level filtering is common in production, and this is precisely the
        // case that must survive it.
        logger.LogWarning(
            "Themia.Notifications: no ISmsSender is configured, so nothing was sent to {Recipient}. "
            + "The development stub handled this send; register a real provider to deliver messages.",
            RecipientRedaction.Mask(message.Recipient));

        return Task.FromResult(NotificationResult.NoProviderConfigured(
            "No ISmsSender is configured; the Themia.Notifications development stub handled this send."));
    }
}
