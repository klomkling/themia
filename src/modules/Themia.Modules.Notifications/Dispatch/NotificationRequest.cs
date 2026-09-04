using Themia.Notifications;

namespace Themia.Modules.Notifications.Dispatch;

/// <summary>An app's request to notify a recipient across one or more channels.</summary>
public sealed class NotificationRequest
{
    /// <summary>Recipient user id (for preference resolution and in-app).</summary>
    public required string UserId { get; init; }

    /// <summary>Channels to attempt (subject to preferences).</summary>
    public required IReadOnlyList<NotificationChannel> Channels { get; init; }

    /// <summary>Email address / phone / push token, by channel. In-app ignores this.</summary>
    public IReadOnlyDictionary<NotificationChannel, string>? Recipients { get; init; }

    /// <summary>Subject (email / in-app title).</summary>
    public string? Subject { get; init; }

    /// <summary>Pre-rendered body, or null to render Template+Model.</summary>
    public string? Body { get; init; }

    /// <summary>Handlebars template source (used when Body is null).</summary>
    public string? Template { get; init; }

    /// <summary>Template model.</summary>
    public object? Model { get; init; }

    /// <summary>Optional future-send time (outbox only).</summary>
    public DateTimeOffset? ScheduledFor { get; init; }

    /// <summary>Carbon-copy recipients (email). Null by default.</summary>
    /// <remarks>
    /// Carried on every outbox row this request produces, including channels with no copy concept —
    /// their senders ignore it, the same way <c>LoggerEmailSender</c> does. Validated when the row is
    /// rehydrated into a <see cref="NotificationMessage"/>, not here, so one set of rules applies whether
    /// a message is sent directly or queued.
    /// </remarks>
    public IReadOnlyList<string>? Cc { get; init; }

    /// <summary>Blind-carbon-copy recipients (email). Null by default.</summary>
    public IReadOnlyList<string>? Bcc { get; init; }

    /// <summary>The text/plain alternative to an HTML <see cref="Body"/>, making the mail
    /// <c>multipart/alternative</c>. Null by default.</summary>
    public string? PlainTextBody { get; init; }

    /// <summary>Extra per-message headers, e.g. <c>X-SES-CONFIGURATION-SET</c>. Null by default.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
