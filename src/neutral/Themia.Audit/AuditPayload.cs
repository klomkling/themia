using System.Text.Json;

namespace Themia.Audit;

/// <summary>
/// Helpers for supplying pre-serialized or structured payloads to <see cref="IAuditRecorder"/> without
/// deserializing and re-serializing domain objects.
/// </summary>
public static class AuditPayload
{
    /// <summary>
    /// Creates a payload from an already-serialized JSON string. The recorder parses it to validate it is
    /// an object or array and runs it through property-level redaction, closing the bare-string hole while
    /// avoiding domain object deserialization.
    /// </summary>
    /// <param name="rawJson">Valid JSON string representing an object or array.</param>
    /// <returns>A parsed <see cref="JsonDocument"/> which the caller is responsible for disposing.</returns>
    /// <exception cref="ArgumentException"><paramref name="rawJson"/> is null, whitespace, or invalid JSON.</exception>
    public static JsonDocument FromRawJson(string rawJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawJson);
        try
        {
            var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object && doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                doc.Dispose();
                throw new ArgumentException(
                    "Raw JSON payload must have an object or array root so property-level redaction can walk its fields.",
                    nameof(rawJson));
            }

            return doc;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The supplied raw JSON payload is not valid JSON.", nameof(rawJson), ex);
        }
    }

    /// <summary>
    /// Creates a structured Before/After change payload (<c>{"before": ..., "after": ...}</c>).
    /// </summary>
    /// <param name="before">State before the change, or <see langword="null"/>.</param>
    /// <param name="after">State after the change, or <see langword="null"/>.</param>
    public static object Diff(object? before, object? after) => new { before, after };
}
