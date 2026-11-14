namespace Themia.Conversations;

/// <summary>How one drain call ended.</summary>
/// <remarks>An enum rather than a bool so a later state breaks an exhaustive <c>switch</c> instead of compiling silently at every existing call site.</remarks>
public enum DrainOutcome
{
    /// <summary>The drain ran. Individual recipients may still have failed; see <see cref="DrainResult.Failed"/>.</summary>
    Completed = 0,

    /// <summary>No <see cref="IConversationNotificationSubscriber"/> is registered, so nothing was claimed or marked.</summary>
    SubscriberMissing = 1,
}

/// <summary>What one drain call does.</summary>
/// <param name="BatchSize">The most recipients to notify in this call (1 to 1000). Call again to continue.</param>
public sealed record DrainOptions(int BatchSize = 100);

/// <summary>The result of <see cref="IConversationNotifications.DrainAsync"/>.</summary>
/// <param name="Outcome">How the drain ended.</param>
/// <param name="Delivered">Recipients the subscriber accepted and whose cursor moved.</param>
/// <param name="Skipped">Claimed recipients who had nothing unread by the time the batch was built (they read in the meantime).</param>
/// <param name="Failed">Recipients whose subscriber call threw; their cursor did not move and the next drain retries them. This is the alarm: each is logged at Warning.</param>
/// <param name="LeaseLost">Recipients the subscriber accepted but whose lease another drain had taken over before the mark; not counted as delivered here, and the other drain owns them now.</param>
public sealed record DrainResult(DrainOutcome Outcome, int Delivered, int Skipped, int Failed, int LeaseLost)
{
    /// <summary>Whether the drain ran.</summary>
    public bool Completed => Outcome == DrainOutcome.Completed;
}

/// <summary>One batched notification: a recipient has unread messages in one conversation. Ids and counts only, never message text or contact data.</summary>
/// <param name="BatchId">Stable for (conversation, recipient, <paramref name="LastSeq"/>): a redelivery after a crash carries the same id when no message arrived since, so the subscriber dedupes on it. A dedupe key only: derived from ids and a sequence number, so never a secret or a capability.</param>
/// <param name="TenantId">The tenant.</param>
/// <param name="ConversationId">The conversation.</param>
/// <param name="ParticipantId">The recipient participant; the app resolves its contact and channel.</param>
/// <param name="UnreadCount">Messages from others the recipient has not read and was not told about before.</param>
/// <param name="LastSeq">The sequence number of the newest of them.</param>
public sealed record ConversationNotification(Guid BatchId, string TenantId, Guid ConversationId, Guid ParticipantId, int UnreadCount, long LastSeq);

/// <summary>
/// Receives the batched notifications. The app implements it and sends through its own channel (for example <c>Themia.Notifications</c>); Conversations sends nothing itself.
/// </summary>
/// <remarks>
/// Delivery is at least once. Keep a durable dedupe keyed on <see cref="ConversationNotification.BatchId"/> with a lifetime of at least
/// <see cref="ConversationOptions.NotifyLeaseDuration"/> plus <see cref="ConversationOptions.NotifyDebounce"/>. Do not throw for a permanent failure (log it and return):
/// a throw leaves the recipient due and the next drain tries again.
/// </remarks>
public interface IConversationNotificationSubscriber
{
    /// <summary>Sends one batched notification.</summary>
    /// <param name="notification">The batch.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task NotifyAsync(ConversationNotification notification, CancellationToken cancellationToken = default);
}

/// <summary>
/// Drains the unread messages of every tenant to the <see cref="IConversationNotificationSubscriber"/>, debounced per recipient. The app schedules the call from its own
/// job (for example on <c>Themia.Scheduling</c>); more than one node may run it at once.
/// </summary>
/// <remarks>Needs the default <see cref="IConversationEventSink"/>: an app that replaces it writes no events, so the drain finds nothing.</remarks>
public interface IConversationNotifications
{
    /// <summary>Notifies the recipients whose oldest unread message is older than <see cref="ConversationOptions.NotifyDebounce"/>.</summary>
    /// <param name="options">The batch size, or <see langword="null"/> for the default.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="DrainOptions.BatchSize"/> is outside 1 to 1000.</exception>
    Task<DrainResult> DrainAsync(DrainOptions? options = null, CancellationToken cancellationToken = default);
}
