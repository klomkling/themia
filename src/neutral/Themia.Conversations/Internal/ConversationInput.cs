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

    public static void Subject(ConversationSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        Key(subject.SubjectType, nameof(ConversationSubject.SubjectType), ConversationSchemaMigration.SubjectTypeWidth);
        Key(subject.SubjectId, nameof(ConversationSubject.SubjectId), ConversationSchemaMigration.SubjectIdWidth);
    }

    public static void Actor(ConversationActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.UserRef is not null)
        {
            Key(actor.UserRef, nameof(ConversationActor.UserRef), ConversationSchemaMigration.UserRefWidth);
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
