namespace Themia.Conversations.AspNetCore;

/// <summary>Who is calling and for which tenant, as the host's <see cref="IConversationActorResolver"/> established it.</summary>
/// <param name="TenantId">The tenant the call is for.</param>
/// <param name="Actor">The member, partner or staff actor. Guests are not resolved by the host; the package reads the guest cookie itself.</param>
public sealed record ConversationCaller(string TenantId, ConversationActor Actor);
