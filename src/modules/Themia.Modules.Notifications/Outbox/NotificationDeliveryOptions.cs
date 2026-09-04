using System.Text.Json;
using System.Text.Json.Serialization;

using Themia.Notifications;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// The per-message email options an outbox row carries beyond recipient/subject/body, stored as JSON in
/// the single <c>delivery_options</c> column.
/// </summary>
/// <remarks>
/// One JSON column rather than four typed ones, deliberately. The claim path in each engine dialect maps
/// its result with a POSITIONAL tuple; four extra columns would put five consecutive <c>string?</c>
/// values beside each other, where swapping <c>cc</c> and <c>bcc</c> compiles, passes a test that only
/// checks "the copies arrived", and sends the blind copies visibly. A JSON object names its fields, so
/// that swap is unrepresentable, and a fifth option later needs no migration.
/// <para>
/// This is the only place the JSON shape is known. Dialects carry an opaque string.
/// </para>
/// </remarks>
public sealed class NotificationDeliveryOptions
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Carbon-copy recipients, or <see langword="null"/> when none were set.</summary>
    public IReadOnlyList<string>? Cc { get; init; }

    /// <summary>Blind-carbon-copy recipients, or <see langword="null"/> when none were set.</summary>
    public IReadOnlyList<string>? Bcc { get; init; }

    /// <summary>The text/plain alternative to an HTML body, or <see langword="null"/>.</summary>
    public string? PlainTextBody { get; init; }

    /// <summary>Extra per-message headers, or <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Serializes the options for storage, or returns <see langword="null"/> when nothing was set.
    /// </summary>
    /// <param name="cc">Carbon-copy recipients.</param>
    /// <param name="bcc">Blind-carbon-copy recipients.</param>
    /// <param name="plainTextBody">The text/plain alternative.</param>
    /// <param name="headers">Extra per-message headers.</param>
    /// <returns>The JSON to store, or <see langword="null"/> to leave the column NULL.</returns>
    /// <remarks>
    /// Null rather than <c>"{}"</c> when there is nothing to store, so a row that sets no options is
    /// byte-identical to one enqueued before this column existed.
    /// </remarks>
    public static string? Serialize(
        IReadOnlyList<string>? cc,
        IReadOnlyList<string>? bcc,
        string? plainTextBody,
        IReadOnlyDictionary<string, string>? headers)
    {
        var hasAny = cc is { Count: > 0 }
            || bcc is { Count: > 0 }
            || !string.IsNullOrWhiteSpace(plainTextBody)
            || headers is { Count: > 0 };

        if (!hasAny) return null;

        return JsonSerializer.Serialize(
            new NotificationDeliveryOptions
            {
                Cc = cc is { Count: > 0 } ? cc : null,
                Bcc = bcc is { Count: > 0 } ? bcc : null,
                PlainTextBody = string.IsNullOrWhiteSpace(plainTextBody) ? null : plainTextBody,
                Headers = headers is { Count: > 0 } ? headers : null,
            },
            SerializerOptions);
    }

    /// <summary>Reads stored options back, or <see langword="null"/> when the column held nothing.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The options, or <see langword="null"/>.</returns>
    /// <exception cref="JsonException">
    /// The column holds text that is not valid JSON for this shape. Deliberately propagated rather than
    /// swallowed: a corrupt row is a permanent condition the dispatcher dead-letters, and returning null
    /// would instead send the message with its options silently dropped — the exact failure this feature
    /// exists to remove.
    /// </exception>
    public static NotificationDeliveryOptions? Deserialize(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<NotificationDeliveryOptions>(json, SerializerOptions);

    /// <summary>Returns <paramref name="message"/> with these options applied.</summary>
    /// <param name="message">The message rebuilt from the outbox row's own columns.</param>
    /// <returns>A copy carrying the stored options.</returns>
    /// <exception cref="ArgumentException">
    /// A stored value fails <see cref="NotificationMessage"/>'s validation — a header carrying CR/LF, or a
    /// blank address. Rehydration re-runs the same checks, so an injected value cannot enter through the
    /// outbox any more than through a direct send.
    /// </exception>
    public NotificationMessage ApplyTo(NotificationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new NotificationMessage
        {
            Channel = message.Channel,
            Recipient = message.Recipient,
            Subject = message.Subject,
            Body = message.Body,
            Template = message.Template,
            Model = message.Model,
            Cc = Cc,
            Bcc = Bcc,
            PlainTextBody = PlainTextBody,
            Headers = Headers,
        };
    }
}
