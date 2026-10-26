namespace Themia.Notifications.Providers;

/// <summary>SMTP provider configuration for <see cref="SmtpEmailSender"/>.</summary>
/// <remarks>
/// <c>System.Net.Mail.SmtpClient</c> is not recommended for new development (.NET DE0005): it supports
/// STARTTLS (<see cref="UseSsl"/> → <c>EnableSsl</c>) on all platforms, but lacks implicit SSL/SMTPS
/// (port 465) and modern SASL auth such as OAuth2/XOAUTH2. Hosts that need those should register a
/// custom <see cref="Themia.Notifications.IEmailSender"/> backed by a library like MailKit instead of
/// this provider.
/// </remarks>
public sealed class SmtpEmailOptions
{
    /// <summary>SMTP host. Required (ignored when <see cref="PickupDirectory"/> is set).</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>SMTP port. Default 587 (submission).</summary>
    public int Port { get; set; } = 587;
    /// <summary>Use STARTTLS/SSL. Default <see langword="true"/>.</summary>
    public bool UseSsl { get; set; } = true;
    /// <summary>Username for SMTP auth. Null for anonymous.</summary>
    public string? UserName { get; set; }
    /// <summary>Password for SMTP auth.</summary>
    public string? Password { get; set; }
    /// <summary>The From address. Required.</summary>
    public string FromAddress { get; set; } = string.Empty;
    /// <summary>The From display name. Optional.</summary>
    public string? FromDisplayName { get; set; }
    /// <summary>Whether bodies are HTML. Default <see langword="true"/>.</summary>
    public bool IsBodyHtml { get; set; } = true;
    /// <summary>When set, emails are written as <c>.eml</c> files to this directory instead of sent
    /// (System.Net.Mail pickup-directory delivery). For tests / local dev.</summary>
    public string? PickupDirectory { get; set; }

    /// <summary>
    /// The media types an attachment may have (compared without parameters, case-insensitively). Empty (the default) allows <c>application/pdf</c> only;
    /// once any type is listed, ONLY the listed types are allowed, so a list in configuration replaces the default and <c>application/pdf</c> must be
    /// listed again if it is still wanted.
    /// </summary>
    public ISet<string> AllowedAttachmentContentTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The most all attachments of one message may weigh together, in bytes. Default 7 MiB: base64 grows a file by a third, and the whole
    /// message must stay under the 10 MB many providers (Amazon SES among them) accept.
    /// </summary>
    public long MaxAttachmentBytes { get; set; } = 7 * 1024 * 1024;
}
