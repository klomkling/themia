using Microsoft.Extensions.Logging;

namespace Themia.Notifications.Providers;

/// <summary>
/// Development <see cref="IEmailSender"/> that logs instead of sending. Never contacts a server, and
/// reports <see cref="NotificationResult.NoProviderConfigured"/> so a caller is never told a message was
/// delivered when none was.
/// </summary>
internal sealed class LoggerEmailSender(ILogger<LoggerEmailSender> logger) : IEmailSender
{
    public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Warning, not Information: this line is the only signal that a host expecting real delivery is
        // getting none. Information-level filtering is common in production, and this is precisely the
        // case that must survive it.
        //
        // The SUBJECT is deliberately not logged. It was, until raising this line to Warning made it
        // survive the production filtering that used to hide it — and subjects routinely carry PII or
        // account context ("Password reset for jane.doe@acme.com", "Invoice INV-2214 for Acme Ltd"). The
        // masked recipient is enough to identify which send this was.
        logger.LogWarning(
            "Themia.Notifications: no IEmailSender is configured, so nothing was sent to {Recipient}. "
            + "The development stub handled this send; register a real provider to deliver messages.",
            RecipientRedaction.Mask(message.Recipient));

        return Task.FromResult(NotificationResult.NoProviderConfigured(
            "No IEmailSender is configured; the Themia.Notifications development stub handled this send."));
    }
}
