namespace Themia.Conversations;

/// <summary>Who is acting. The tenant is never part of the actor: every call takes it explicitly.</summary>
/// <param name="Kind">The kind of party.</param>
/// <param name="UserRef">The opaque id of a member or partner agent in its own app's namespace; <see langword="null"/> for a guest or the system.</param>
/// <param name="Scope">What a partner agent was granted (for example the partner and the job), so the policy can accept it; <see langword="null"/> otherwise.</param>
/// <param name="ParticipantId">For a guest, the participant of the verified guest session. Never trusted: the store re-resolves the participant inside its transaction.</param>
public sealed record ConversationActor(ConversationActorKind Kind, string? UserRef = null, string? Scope = null, Guid? ParticipantId = null);

/// <summary>What an access decision is about.</summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="ConversationId">The conversation, or <see langword="null"/> when the action is not on an existing one (opening a subject).</param>
public sealed record ConversationTarget(string TenantId, Guid? ConversationId);
