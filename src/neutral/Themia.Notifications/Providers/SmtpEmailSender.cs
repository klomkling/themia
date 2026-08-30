using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;

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

        var body = message.Body ?? (message.Template is not null
            ? renderer.Render(message.Template, message.Model ?? new { })
            : string.Empty);

        var subject = Merge(message.Subject, message.Model) ?? string.Empty;
        var plainText = Merge(message.PlainTextBody, message.Model);

        using var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromDisplayName),
            Subject = subject,
        };
        mail.To.Add(message.Recipient);

        if (plainText is null)
        {
            mail.Body = body;
            mail.IsBodyHtml = options.IsBodyHtml;
        }
        else
        {
            // multipart/alternative. Body stays unset: with it set, MailMessage emits a THIRD part —
            // measured as text/plain; charset=us-ascii, inserted ahead of both views and carrying the HTML
            // source as literal text — which is not what "here are two forms of one message" means.
            //
            // IsBodyHtml is deliberately not consulted. Supplying a text alternative declares Body to be
            // the HTML form, so a deployment-wide flag cannot silently discard the alternative on the
            // hosts most likely to have set it wrong.
            //
            // Order matters: RFC 2046 orders alternatives by INCREASING preference, so the richest form
            // goes last. Reversed, a client honouring the order shows plain text to everyone.
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                plainText, Encoding.UTF8, MediaTypeNames.Text.Plain));
            mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
                body, Encoding.UTF8, MediaTypeNames.Text.Html));
        }

        // Address format is MailAddress's job — a malformed entry throws FormatException, which the
        // outbox dispatcher already treats as permanent. NotificationMessage has ruled out CR/LF.
        if (message.Cc is not null)
        {
            foreach (var cc in message.Cc)
                mail.CC.Add(cc);
        }

        if (message.Bcc is not null)
        {
            foreach (var bcc in message.Bcc)
                mail.Bcc.Add(bcc);
        }

        // Verbatim: NotificationMessage validated these on assignment (no CR/LF, no reserved name), so
        // there is nothing left to sanitise here and a second check would only drift from that one.
        if (message.Headers is not null)
        {
            foreach (var (name, value) in message.Headers)
                mail.Headers.Add(name, value);
        }

        using var client = CreateClient();
        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
        return NotificationResult.Success();
    }

    // Renders only when the text actually carries a token, so a plain string is not paid for. Shared by
    // the subject and the plain-text part: both are written by the caller and both reach the recipient,
    // so an unmerged {{token}} leaks either way.
    private string? Merge(string? text, object? model) =>
        text is not null && text.Contains("{{", StringComparison.Ordinal)
            ? renderer.Render(text, model ?? new { })
            : text;

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
