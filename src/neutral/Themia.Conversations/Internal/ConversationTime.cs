namespace Themia.Conversations.Internal;

/// <summary>Reads a timestamp column back as UTC whatever the engine's driver returns (<c>DateTime</c> on MySQL, <c>DateTimeOffset</c> on SQL Server, either on PostgreSQL).</summary>
internal static class ConversationTime
{
    public static DateTimeOffset ToUtc(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime { Kind: DateTimeKind.Local } local => new DateTimeOffset(local.ToUniversalTime(), TimeSpan.Zero),
        DateTime utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero),
        _ => throw new InvalidOperationException($"Unexpected timestamp type {value.GetType().Name}."),
    };
}
