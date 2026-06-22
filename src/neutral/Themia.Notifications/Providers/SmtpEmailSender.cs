using System.Net;
using System.Net.Mail;

namespace Themia.Notifications.Providers;

/// <summary><see cref="IEmailSender"/> over <c>System.Net.Mail.SmtpClient</c>. Renders the body from
/// <see cref="NotificationMessage.Template"/> + <see cref="NotificationMessage.Model"/> when no
/// pre-rendered <see cref="NotificationMessage.Body"/> is supplied.</summary>
internal sealed class SmtpEmailSender(SmtpEmailOptions options, INotificationTemplateRenderer renderer) : IEmailSender
{
    public async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var body = message.Body ?? (message.Template is not null ? renderer.Render(message.Template, message.Model ?? new { }) : string.Empty);
        var subject = message.Subject is not null && message.Model is not null && message.Body is null
            ? renderer.Render(message.Subject, message.Model)   // subject may also be a template
            : message.Subject ?? string.Empty;

        using var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromDisplayName),
            Subject = subject,
            Body = body,
            IsBodyHtml = options.IsBodyHtml,
        };
        mail.To.Add(message.Recipient);

        using var client = CreateClient();
        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
        return NotificationResult.Success();
    }

    private SmtpClient CreateClient()
    {
        if (!string.IsNullOrEmpty(options.PickupDirectory))
        {
            return new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = options.PickupDirectory,
            };
        }

        var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.UseSsl };
        if (!string.IsNullOrEmpty(options.UserName))
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        return client;
    }
}
