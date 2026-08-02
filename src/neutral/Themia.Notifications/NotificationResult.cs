namespace Themia.Notifications;

/// <summary>How a send attempt ended.</summary>
/// <remarks>
/// An enum rather than a second bool alongside <see cref="NotificationResult.Succeeded"/>: a new state
/// added as a bool compiles cleanly at every existing <c>if (result.Succeeded)</c> site, so nothing forces
/// a consumer to revisit its mapping. That is precisely how <see cref="NotConfigured"/> was first shipped
/// without <c>NotificationOutboxDispatcher</c> being updated, which dead-lettered every notification on a
/// host with no provider. A <c>switch</c> over this enum fails to compile when a state is unhandled.
/// </remarks>
public enum NotificationOutcome
{
    /// <summary>The provider accepted the message.</summary>
    Sent = 0,

    /// <summary>The provider rejected the message, or the send failed.</summary>
    Failed = 1,

    /// <summary>
    /// Nothing was sent because no real provider is configured — the development stub ran instead. Not a
    /// failure of the message: retrying it cannot help, because configuration does not change between
    /// attempts.
    /// </summary>
    NotConfigured = 2,
}

/// <summary>The outcome of a send attempt.</summary>
public sealed class NotificationResult
{
    private NotificationResult(NotificationOutcome outcome, string? providerMessageId, string? error)
    {
        Outcome = outcome;
        ProviderMessageId = providerMessageId;
        Error = error;
    }

    /// <summary>How the attempt ended. Switch over this rather than reading <see cref="Succeeded"/> when
    /// the three states need different handling.</summary>
    public NotificationOutcome Outcome { get; }

    /// <summary>Whether the provider accepted the message — true only for <see cref="NotificationOutcome.Sent"/>.</summary>
    public bool Succeeded => Outcome == NotificationOutcome.Sent;

    /// <summary>
    /// True when nothing was sent because no provider is configured. <see cref="Succeeded"/> is
    /// <see langword="false"/> in this state.
    /// </summary>
    /// <remarks>
    /// Exists so "I deliberately did not send this" and "I sent this" are not the same value. Running
    /// without a provider stays a supported state (a dev box, or a deployment with a channel deliberately
    /// disabled) — it is simply no longer indistinguishable from delivery.
    /// <para>
    /// Do NOT fold this into a success when recording or reporting delivery. It is safe to treat as
    /// "handled" for control flow; it is wrong to treat as "delivered" in an audit trail, which would
    /// restore the very defect this state was added to remove.
    /// </para>
    /// </remarks>
    public bool NotConfigured => Outcome == NotificationOutcome.NotConfigured;

    /// <summary>The provider's message id, when it returns one.</summary>
    public string? ProviderMessageId { get; }

    /// <summary>
    /// Describes why the message was not sent. Set for both <see cref="NotificationOutcome.Failed"/> (a
    /// provider rejection) and <see cref="NotificationOutcome.NotConfigured"/> (a deliberate non-send), so
    /// a non-null value here does not by itself mean something went wrong — check <see cref="Outcome"/>.
    /// </summary>
    public string? Error { get; }

    /// <summary>Creates a success result.</summary>
    /// <param name="providerMessageId">The provider's message id, when it returns one.</param>
    /// <returns>A <see cref="NotificationOutcome.Sent"/> result.</returns>
    public static NotificationResult Success(string? providerMessageId = null)
        => new(NotificationOutcome.Sent, providerMessageId, null);

    /// <summary>Creates a failure result. Built-in senders throw on provider failure instead; this is
    /// for custom senders that represent a provider rejection as a result rather than an exception.</summary>
    /// <param name="error">The failure description.</param>
    /// <returns>A <see cref="NotificationOutcome.Failed"/> result.</returns>
    /// <exception cref="ArgumentException"><paramref name="error"/> is null or empty.</exception>
    public static NotificationResult Failure(string error)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        return new(NotificationOutcome.Failed, null, error);
    }

    /// <summary>
    /// Creates a "nothing was sent, no provider is configured" result. Used by the development stubs so
    /// they cannot report a delivery they did not make.
    /// </summary>
    /// <param name="reason">Why nothing was sent, surfaced through <see cref="Error"/>.</param>
    /// <returns>A <see cref="NotificationOutcome.NotConfigured"/> result.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or empty.</exception>
    public static NotificationResult NoProviderConfigured(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        return new(NotificationOutcome.NotConfigured, null, reason);
    }
}
