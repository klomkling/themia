using Themia.Challenges;

namespace Themia.Conversations;

/// <summary>
/// Guest access: invites by contact and the sessions they open. <see cref="Peppers"/> and <see cref="CurrentPepperId"/> are required; <see cref="Validate"/> runs at
/// registration. The contact hash format is permanent once a guest row exists: <b>never remove a pepper id while any participant row still carries it</b>, because
/// a guest hashed under a removed key is no longer found (a re-invite would add a second participant, and a data-subject lookup would miss the person).
/// </summary>
public sealed class ConversationGuestOptions
{
    /// <summary>The challenge purpose of a guest link. Register it on <see cref="ChallengeOptions"/> with <see cref="ConversationGuestChallengeExtensions.ConfigureConversationGuestPurpose"/>.</summary>
    public const string ChallengePurpose = "conversation-guest";

    /// <summary>The shortest pepper, in bytes.</summary>
    public const int MinPepperBytes = 32;

    /// <summary>The widest pepper id the schema holds.</summary>
    public const int PepperIdWidth = 40;

    /// <summary>Server-side secrets by id. A contact is hashed (HMAC-SHA256) under <see cref="CurrentPepperId"/>; a lookup tries every id here.</summary>
    public IDictionary<string, byte[]> Peppers { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>The id in <see cref="Peppers"/> new hashes use.</summary>
    public string? CurrentPepperId { get; set; }

    /// <summary>
    /// How long a guest session lasts. Default 7 days, an interim value: until a guest can ask for a new link themselves, a session that ended needs a member to re-invite
    /// them, so the owner may want it longer or shorter.
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The most active guests one conversation may hold. Default 50. The issue-side limits of <c>Themia.Challenges</c> are per participant, so this is the ceiling on one conversation.</summary>
    public int MaxGuestsPerConversation { get; set; } = 50;

    internal void Validate()
    {
        Require(Peppers.Count > 0, nameof(Peppers), "must hold at least one pepper");
        foreach (var (id, key) in Peppers)
        {
            Require(!string.IsNullOrWhiteSpace(id) && id.Length <= PepperIdWidth && id == id.Trim(), nameof(Peppers), $"the id '{id}' must be 1 to {PepperIdWidth} characters without padding");
            Require(key is { Length: >= MinPepperBytes }, nameof(Peppers), $"the pepper '{id}' must be at least {MinPepperBytes} bytes");
        }

        Require(CurrentPepperId is not null && Peppers.ContainsKey(CurrentPepperId), nameof(CurrentPepperId), "must name an id in Peppers");
        Require(SessionLifetime > TimeSpan.Zero, nameof(SessionLifetime), "must be positive");
        Require(MaxGuestsPerConversation > 0, nameof(MaxGuestsPerConversation), "must be positive");
    }

    private static void Require(bool condition, string option, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{nameof(ConversationGuestOptions)}.{option} {message}.");
        }
    }
}

/// <summary>Registers the guest-link purpose with <c>Themia.Challenges</c>.</summary>
public static class ConversationGuestChallengeExtensions
{
    /// <summary>
    /// Configures the <see cref="ConversationGuestOptions.ChallengePurpose"/> purpose: an opaque 32-byte token, one live link per guest (a re-invite supersedes the earlier
    /// link). Call it inside the <c>AddThemiaChallenges</c> callback.
    /// </summary>
    /// <param name="options">The challenge options.</param>
    /// <param name="linkLifetime">How long a link can be redeemed. Default 72 hours.</param>
    public static void ConfigureConversationGuestPurpose(this ChallengeOptions options, TimeSpan? linkLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ConfigurePurpose(ConversationGuestOptions.ChallengePurpose, purpose =>
        {
            purpose.Format = ChallengeFormat.OpaqueToken(32);
            purpose.Ttl = linkLifetime ?? TimeSpan.FromHours(72);
            purpose.MaxLiveChallenges = 1;
        });
    }
}
