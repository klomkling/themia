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
}
