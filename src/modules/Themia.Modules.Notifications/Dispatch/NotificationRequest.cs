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
}
