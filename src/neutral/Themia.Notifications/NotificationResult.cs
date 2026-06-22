namespace Themia.Notifications;

/// <summary>The outcome of a send attempt.</summary>
public sealed class NotificationResult
{
    private NotificationResult(bool succeeded, string? providerMessageId, string? error)
    {
        Succeeded = succeeded;
        ProviderMessageId = providerMessageId;
        Error = error;
    }

    /// <summary>Whether the provider accepted the message.</summary>
    public bool Succeeded { get; }

    /// <summary>The provider's message id, when it returns one.</summary>
    public string? ProviderMessageId { get; }

    /// <summary>The failure description when <see cref="Succeeded"/> is false.</summary>
    public string? Error { get; }

    /// <summary>Creates a success result.</summary>
    public static NotificationResult Success(string? providerMessageId = null) => new(true, providerMessageId, null);

    /// <summary>Creates a failure result.</summary>
    public static NotificationResult Failure(string error) => new(false, null, error);
}
