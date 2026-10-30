namespace Themia.Conversations;

/// <summary>How a guest is reached.</summary>
public enum GuestContactKind
{
    /// <summary>An e-mail address; compared case-insensitively.</summary>
    Email = 0,

    /// <summary>A phone number in E.164 form (<c>+</c>, then 8 to 15 digits, the first not zero); any other form is refused, because <c>0812345678</c> and <c>+66812345678</c> would otherwise be two guests.</summary>
    Phone = 1,

    /// <summary>A LINE user id; case-sensitive.</summary>
    Line = 2,
}

/// <summary>
/// The contact a guest is invited over. It is hashed with a server-side pepper before it is stored, and never logged. The address has no public member and
/// <see cref="ToString"/> shows the kind only, so logging or destructuring a contact does not write it out.
/// </summary>
public sealed class GuestContact
{
    /// <summary>Wraps a contact.</summary>
    /// <param name="kind">The channel.</param>
    /// <param name="value">The address, number or id.</param>
    public GuestContact(GuestContactKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Kind = kind;
        Value = value;
    }

    /// <summary>The channel.</summary>
    public GuestContactKind Kind { get; }

    internal string Value { get; }

    /// <inheritdoc />
    public override string ToString() => $"GuestContact {{ Kind = {Kind}, Value = [redacted] }}";
}

/// <summary>The one-time secret of a guest link. Hand <see cref="Reveal"/> to the app's sender; it is a method so a logger or serialiser that reads properties cannot write it out.</summary>
public sealed class GuestLinkToken
{
    private readonly string value;

    internal GuestLinkToken(string value) => this.value = value;

    /// <summary>The token to put in the link. Never store or log it.</summary>
    public string Reveal() => value;

    /// <inheritdoc />
    public override string ToString() => "[redacted]";
}

/// <summary>How <see cref="IConversationGuests.InviteAsync"/> ended.</summary>
public enum GuestInviteOutcome
{
    /// <summary>A new guest was added and a link issued.</summary>
    Invited = 0,

    /// <summary>The contact was a guest of the conversation already; a new link was issued and the earlier one stopped working.</summary>
    Reinvited = 1,

    /// <summary>The contact or display name is malformed.</summary>
    Invalid = 2,

    /// <summary>The policy denied the call, the inviter is not an active participant, the conversation is closed or missing, or the guest left the conversation.</summary>
    Refused = 3,

    /// <summary>The conversation holds <see cref="ConversationGuestOptions.MaxGuestsPerConversation"/> active guests.</summary>
    LimitReached = 4,

    /// <summary>The issue-side limit of <c>Themia.Challenges</c> stopped the link; try later.</summary>
    RateLimited = 5,
}

/// <summary>The result of an invite. <see cref="Token"/> is the secret for the link: hand it to the app's sender and never store or log it.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="ParticipantId">The guest participant, when invited.</param>
/// <param name="Token">The link secret, when invited.</param>
/// <param name="ExpiresAt">When the link stops working.</param>
public sealed record GuestInvite(GuestInviteOutcome Outcome, Guid? ParticipantId, GuestLinkToken? Token, DateTimeOffset? ExpiresAt)
{
    /// <summary>Whether a link was issued.</summary>
    public bool Succeeded => Outcome is GuestInviteOutcome.Invited or GuestInviteOutcome.Reinvited;
}

/// <summary>How <see cref="IGuestAccess.ExchangeAsync"/> ended.</summary>
public enum GuestExchangeOutcome
{
    /// <summary>The token was valid and a session was opened.</summary>
    Established = 0,

    /// <summary>The token is unknown, expired, used, for another tenant, or its guest left or its conversation closed. One outcome for all of them, so the answer is no oracle.</summary>
    Refused = 1,
}

/// <summary>The result of redeeming a guest link. The key is shown once.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="ConversationId">The conversation the session is bound to.</param>
/// <param name="SessionKey">The cookie secret; the store keeps only its hash.</param>
/// <param name="ExpiresAt">When the session ends.</param>
public sealed record GuestSessionResult(GuestExchangeOutcome Outcome, Guid? ConversationId, GuestSessionKey? SessionKey, DateTimeOffset? ExpiresAt)
{
    /// <summary>Whether a session was opened.</summary>
    public bool Succeeded => Outcome == GuestExchangeOutcome.Established;
}

/// <summary>Invites guests by contact.</summary>
public interface IConversationGuests
{
    /// <summary>
    /// Adds the contact as a guest of the conversation (or finds it) and issues a one-time link token. The inviter must be an active participant and pass the policy
    /// (<see cref="ConversationAction.Invite"/>). A re-invite supersedes the earlier link. Throttling per contact and per inviter belongs to the app's sender.
    /// </summary>
    /// <param name="actor">A member or partner agent.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="conversationId">The conversation.</param>
    /// <param name="contact">How to reach the guest.</param>
    /// <param name="displayName">The name other participants see.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<GuestInvite> InviteAsync(ConversationActor actor, string tenantId, Guid conversationId, GuestContact contact, string displayName, CancellationToken cancellationToken = default);
}

/// <summary>Turns a guest link token into a session. Call it from a POST, never a GET: mail and chat link previewers fetch URLs and would spend the token.</summary>
public interface IGuestAccess
{
    /// <summary>
    /// Redeems the token. On success the guest's earlier sessions end and a new one opens. Every failure is one <see cref="GuestExchangeOutcome.Refused"/>, and the token
    /// is spent whatever the outcome.
    /// </summary>
    /// <param name="tenantId">The tenant, from the request.</param>
    /// <param name="token">The token from the link.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<GuestSessionResult> ExchangeAsync(string tenantId, string token, CancellationToken cancellationToken = default);
}

/// <summary>What a viewer may see of a message: applied when a page is read, so the stored text stays whole.</summary>
public interface IConversationMasker
{
    /// <summary>
    /// Returns the message as <paramref name="viewer"/> may see it. Best effort, not a control. An exception makes the store hide the body (fail closed).
    /// </summary>
    /// <param name="viewer">The kind of viewer.</param>
    /// <param name="message">The stored message.</param>
    ConversationMessage Mask(ConversationActorKind viewer, ConversationMessage message);
}
