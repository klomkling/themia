namespace Themia.Conversations;

/// <summary>
/// The app's authorization for conversations. Every service method asks it first. The default registration denies everything, so a conversation
/// is unreachable until the app registers its own policy: fail closed.
/// </summary>
/// <remarks>
/// <see cref="ConversationAction.Post"/> is asked twice: before the post's transaction, and again while Themia holds the conversation row lock in that
/// transaction, so a change of the caller's standing made under the same row lock is seen. The second call needs its own connection while the post holds
/// one. Read only your own tables, never the conversation tables through another connection. On SQL Server, enable <c>READ_COMMITTED_SNAPSHOT</c>, or lock
/// the conversation row before you write rows this policy reads; otherwise the policy waits on your uncommitted write while the post waits on your lock.
/// </remarks>
public interface IConversationAccessPolicy
{
    /// <summary>Whether <paramref name="actor"/> may do <paramref name="action"/> on <paramref name="target"/>.</summary>
    /// <param name="actor">Who is acting.</param>
    /// <param name="target">The tenant and, when it exists, the conversation.</param>
    /// <param name="action">What they want to do.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default);
}
