using Microsoft.AspNetCore.Http;

namespace Themia.Conversations.AspNetCore;

/// <summary>
/// The host's answer to "who is this request?". The package never decides identity: it asks this first, and only when it returns
/// <see langword="null"/> does it look for a guest cookie for the conversation in the route. There is no default; registering none fails startup.
/// </summary>
public interface IConversationActorResolver
{
    /// <summary>Resolves the caller from the host's own authentication, or <see langword="null"/> when the request carries none.</summary>
    /// <param name="context">The request.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<ConversationCaller?> ResolveAsync(HttpContext context, CancellationToken cancellationToken);
}
