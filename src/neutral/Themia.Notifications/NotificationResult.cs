namespace Themia.Notifications;

/// <summary>The outcome of a send attempt.</summary>
public sealed class NotificationResult
{
    private NotificationResult(bool succeeded, bool notConfigured, string? providerMessageId, string? error)
    {
        Succeeded = succeeded;
        NotConfigured = notConfigured;
        ProviderMessageId = providerMessageId;
        Error = error;
    }

    /// <summary>Whether the provider accepted the message.</summary>
    public bool Succeeded { get; }

    /// <summary>
    /// True when nothing was sent because no real provider is configured — the development stub ran
    /// instead. <see cref="Succeeded"/> is <see langword="false"/> in this state.
    /// </summary>
    /// <remarks>
    /// This exists so "I deliberately did not send this" and "I sent this" are not the same value.
    /// Before it, the logger stubs returned <see cref="Success"/> having sent nothing, so a host that
    /// never configured a provider — or configured one whose settings were incomplete — saw every send
    /// succeed while no message was ever delivered, and the caller's own retry and audit logic recorded
    /// deliveries that never happened.
    /// <para>
    /// Running without a provider on purpose is a supported state (a dev box, or a deployment with email
    /// deliberately disabled). Distinguish it from a genuine provider failure with:
    /// <c>if (!result.Succeeded &amp;&amp; !result.NotConfigured) { /* real failure */ }</c>
    /// </para>
    /// </remarks>
    public bool NotConfigured { get; }

    /// <summary>The provider's message id, when it returns one.</summary>
    public string? ProviderMessageId { get; }

    /// <summary>The failure description when <see cref="Succeeded"/> is false.</summary>
    public string? Error { get; }

    /// <summary>Creates a success result.</summary>
    public static NotificationResult Success(string? providerMessageId = null) => new(true, false, providerMessageId, null);

    /// <summary>Creates a failure result. Built-in senders throw on provider failure instead; this is
    /// for custom senders that represent a provider rejection as a result rather than an exception.</summary>
    public static NotificationResult Failure(string error)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        return new(false, false, null, error);
    }

    /// <summary>
    /// Creates a "nothing was sent, no provider is configured" result — <see cref="Succeeded"/> false and
    /// <see cref="NotConfigured"/> true. Used by the development stubs so they cannot report a delivery
    /// they did not make.
    /// </summary>
    /// <param name="reason">Why nothing was sent, surfaced through <see cref="Error"/>.</param>
    /// <returns>A result describing an intentionally-undelivered message.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or empty.</exception>
    public static NotificationResult NoProviderConfigured(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new(false, true, null, reason);
    }
}
