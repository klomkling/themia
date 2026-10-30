namespace Themia.Conversations;

/// <summary>
/// The app's authorization for conversations. Every service method asks it first. The default registration denies everything, so a conversation
/// is unreachable until the app registers its own policy: fail closed.
/// </summary>
public interface IConversationAccessPolicy
{
    /// <summary>Whether <paramref name="actor"/> may do <paramref name="action"/> on <paramref name="target"/>.</summary>
    /// <param name="actor">Who is acting.</param>
    /// <param name="target">The tenant and, when it exists, the conversation.</param>
    /// <param name="action">What they want to do.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default);
}
