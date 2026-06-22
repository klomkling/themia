namespace Themia.Notifications.Providers;

/// <summary>SMTP provider configuration for <see cref="SmtpEmailSender"/>.</summary>
/// <remarks><c>System.Net.Mail.SmtpClient</c> has limited support on Linux/macOS — it does not
/// negotiate modern AUTH/STARTTLS. Production hosts that require those should register a custom
/// <see cref="IEmailSender"/> backed by a library such as MailKit instead of using this provider.</remarks>
public sealed class SmtpEmailOptions
{
    /// <summary>SMTP host. Required (ignored when <see cref="PickupDirectory"/> is set).</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>SMTP port. Default 25.</summary>
    public int Port { get; set; } = 25;
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
