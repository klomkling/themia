namespace Themia.Conversations;

/// <summary>How a retention sweep ended.</summary>
/// <remarks>An enum rather than a bool so a later state breaks an exhaustive <c>switch</c> instead of compiling silently at every existing call site.</remarks>
public enum SweepOutcome
{
    /// <summary>The sweep ran. Individual conversations may still have failed; see <see cref="SweepResult.Failed"/>.</summary>
    Completed = 0,

    /// <summary>No <see cref="ILegalHoldPort"/> is registered and <see cref="ConversationOptions.NoLegalHoldPort"/> is not set, so nothing was purged. Expired conversations were still marked.</summary>
    HoldPortMissing = 1,
}

/// <summary>What a retention sweep does in one call.</summary>
/// <param name="BatchSize">The most conversations to mark and the most to purge in this call (1 to 1000). Call again to continue.</param>
public sealed record SweepOptions(int BatchSize = 100);

/// <summary>The result of <see cref="IConversationRetention.SweepAsync"/>.</summary>
/// <param name="Outcome">How the sweep ended.</param>
/// <param name="Expired">Conversations moved from <see cref="ConversationRetentionState.Active"/> to <see cref="ConversationRetentionState.Expired"/> by this call.</param>
/// <param name="Purged">Conversations whose files and rows were deleted.</param>
/// <param name="HeldSkipped">Conversations the hold port reported as held; they are now <see cref="ConversationRetentionState.LegalHold"/> and untouched.</param>
/// <param name="Failed">Conversations left claimed for purging because a file delete, the hold port or the database failed; the next sweep resumes them.</param>
public sealed record SweepResult(SweepOutcome Outcome, int Expired, int Purged, int HeldSkipped, int Failed)
{
    /// <summary>Whether the purge step ran.</summary>
    public bool Completed => Outcome == SweepOutcome.Completed;
}

/// <summary>How a hold change ended.</summary>
public enum HoldOutcome
{
    /// <summary>The conversation is under legal hold now.</summary>
    Placed = 0,

    /// <summary>The conversation was already under legal hold.</summary>
    AlreadyHeld = 1,

    /// <summary>The hold was lifted; the conversation is <see cref="ConversationRetentionState.Active"/> again.</summary>
    Released = 2,

    /// <summary>The conversation was not under legal hold.</summary>
    NotHeld = 3,

    /// <summary>A purge has claimed the conversation; it can no longer be held.</summary>
    PurgeInProgress = 4,

    /// <summary>No such conversation in this tenant.</summary>
    NotFound = 5,

    /// <summary>The access policy refused the action.</summary>
    Refused = 6,
}

/// <summary>The result of a hold change.</summary>
/// <param name="Outcome">How the call ended.</param>
public sealed record HoldResult(HoldOutcome Outcome)
{
    /// <summary>Whether the conversation is under legal hold after the call.</summary>
    public bool IsHeld => Outcome is HoldOutcome.Placed or HoldOutcome.AlreadyHeld;
}

/// <summary>The app's authority on whether a subject is under a legal hold, asked by the sweep just before it claims a conversation for purging.</summary>
/// <remarks>
/// There is no default: a port that answered "not held" for everything would purge under a hold. Register one, or set <see cref="ConversationOptions.NoLegalHoldPort"/>.
/// An exception is treated as "held or unknown": the conversation is not purged and counts as failed. Place holds through <see cref="IConversationRetention.PlaceHoldAsync"/>
/// as well, so the package knows them.
/// </remarks>
public interface ILegalHoldPort
{
    /// <summary>Whether the subject is under a legal hold.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subject">The conversation's subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> to keep the conversation.</returns>
    ValueTask<bool> IsHeldAsync(string tenantId, ConversationSubject subject, CancellationToken cancellationToken = default);
}

/// <summary>Retention: expire closed conversations, purge them, and hold them back.</summary>
public interface IConversationRetention
{
    /// <summary>Marks the tenant's closed conversations past their retention date as expired and purges the expired ones, a batch at a time.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="options">The batch size.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the call did.</returns>
    Task<SweepResult> SweepAsync(string tenantId, SweepOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Puts an open, closed or expired conversation under legal hold so the sweep skips it.</summary>
    /// <param name="actor">Who asks; the access policy decides with <see cref="ConversationAction.Hold"/>.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<HoldResult> PlaceHoldAsync(ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken = default);

    /// <summary>Lifts a legal hold. The retention date is unchanged, so the next sweep may expire the conversation at once.</summary>
    /// <param name="actor">Who asks; the access policy decides with <see cref="ConversationAction.ReleaseHold"/>.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The outcome.</returns>
    Task<HoldResult> ReleaseHoldAsync(ConversationActor actor, string tenantId, Guid conversationId, CancellationToken cancellationToken = default);
}
