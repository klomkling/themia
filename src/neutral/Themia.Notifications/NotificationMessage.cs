namespace Themia.Notifications;

/// <summary>A single notification to send. Either <see cref="Body"/> is pre-rendered, or
/// <see cref="Template"/> + <see cref="Model"/> are merged by an <c>INotificationTemplateRenderer</c>.</summary>
public sealed class NotificationMessage
{
    /// <summary>The delivery channel.</summary>
    public NotificationChannel Channel { get; init; }

    /// <summary>The recipient address (email address, phone number, or user id for in-app).</summary>
    public string Recipient { get; init; } = string.Empty;

    /// <summary>Subject line (email); ignored by channels without a subject.</summary>
    public string? Subject { get; init; }

    /// <summary>Pre-rendered body. When set, it is used verbatim and <see cref="Template"/> is ignored.</summary>
    public string? Body { get; init; }

    /// <summary>Handlebars template source, merged with <see cref="Model"/> when <see cref="Body"/> is null.</summary>
    public string? Template { get; init; }

    /// <summary>The model merged into <see cref="Template"/>.</summary>
    public object? Model { get; init; }

    /// <summary>Optional channel/provider metadata (e.g. cc, sender id).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
