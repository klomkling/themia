namespace Themia.Conversations.Internal;

/// <summary>The default <see cref="IConversationAccessPolicy"/>: nobody may do anything until the app says otherwise.</summary>
internal sealed class DenyAllConversationAccessPolicy : IConversationAccessPolicy
{
    public ValueTask<bool> CanAsync(ConversationActor actor, ConversationTarget target, ConversationAction action, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
}
