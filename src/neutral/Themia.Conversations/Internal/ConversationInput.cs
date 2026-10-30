using Themia.Conversations.Migrations;

namespace Themia.Conversations.Internal;

/// <summary>
/// Argument checks that run before any database call. MySQL and SQL Server compare strings as if trailing spaces were absent while PostgreSQL does not, so
/// <c>"acme"</c> and <c>"acme "</c> would be one tenant on two engines and two on the third: padded values are refused outright.
/// </summary>
internal static class ConversationInput
{
    public const int ClientMessageIdWidth = 64;

    public static void Tenant(string value, string name) => Key(value, name, ConversationSchemaMigration.TenantIdWidth);

    /// <summary>Whether <paramref name="value"/> would pass <see cref="Tenant"/>; for input that arrives from an anonymous caller, where a refusal beats an exception.</summary>
    public static bool IsTenant(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= ConversationSchemaMigration.TenantIdWidth && !char.IsWhiteSpace(value[0]) && !char.IsWhiteSpace(value[^1]);

    public static void Subject(ConversationSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        Key(subject.SubjectType, nameof(ConversationSubject.SubjectType), ConversationSchemaMigration.SubjectTypeWidth);
        Key(subject.SubjectId, nameof(ConversationSubject.SubjectId), ConversationSchemaMigration.SubjectIdWidth);
    }

    /// <summary>Longest user reference an actor may carry: the stored value gains a two-character kind prefix.</summary>
    public const int MaxUserRefLength = ConversationSchemaMigration.UserRefWidth - 2;

    /// <summary>
    /// The value stored in <c>user_ref</c>. A member and a partner agent live in different id spaces, so <c>member 42</c> and <c>partner 42</c> must be two people:
    /// the kind is part of the stored key (<c>m:</c> or <c>p:</c>), which also keeps the unique key per conversation exact. A data-subject lookup must use the same form.
    /// </summary>
    public static string? StoredUserRef(ConversationActor actor) =>
        actor.UserRef is null ? null : (actor.Kind == ConversationActorKind.Partner ? "p:" : "m:") + actor.UserRef;

    public static void Actor(ConversationActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.UserRef is not null)
        {
            Key(actor.UserRef, nameof(ConversationActor.UserRef), MaxUserRefLength);
        }
    }

    public static void ClientMessageId(string value) => Key(value, nameof(PostMessage.ClientMessageId), ClientMessageIdWidth);

    /// <summary>Whether <paramref name="name"/> is a displayable name: not blank, within the cap, no control characters, no markup.</summary>
    public static bool IsDisplayName(string? name, int maxLength) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= maxLength && name == name.Trim() && !name.Any(c => char.IsControl(c) || c is '<' or '>');

    private static void Key(string value, string name, int width)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > width)
        {
            throw new ArgumentException($"{name} is longer than {width} characters.", name);
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            throw new ArgumentException($"{name} must not start or end with whitespace.", name);
        }
    }
}
