using System.Data.Common;

namespace Themia.Conversations.Internal;

/// <summary>The default <see cref="IConversationLifecycleSink"/>: records nothing. <c>conversation_events</c> is not used, because a purge deletes it.</summary>
internal sealed class DefaultConversationLifecycleSink : IConversationLifecycleSink
{
    public Task RaiseAsync(DbConnection connection, DbTransaction transaction, ConversationLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
