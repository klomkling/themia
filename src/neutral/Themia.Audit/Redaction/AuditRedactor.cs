using System.Text;
using System.Text.Json;

namespace Themia.Audit.Redaction;

/// <summary>
/// Default <see cref="IAuditRedactor"/>. Walks the document with <see cref="JsonDocument"/> and rewrites
/// it with <see cref="Utf8JsonWriter"/>, replacing the value of any property whose name matches
/// <see cref="AuditRedactionOptions"/> — at any depth, including inside arrays of objects — with the
/// literal string <c>"[redacted]"</c>, regardless of the original value's JSON kind.
/// </summary>
public sealed class AuditRedactor : IAuditRedactor
{
    private const string RedactedValue = "[redacted]";

    private readonly AuditRedactionOptions options;

    /// <summary>Creates a redactor using <paramref name="options"/>.</summary>
    /// <param name="options">The patterns to treat as sensitive.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public AuditRedactor(AuditRedactionOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Redact(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteElement(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void WriteElement(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (options.Matches(property.Name))
                    {
                        writer.WriteStringValue(RedactedValue);
                    }
                    else
                    {
                        WriteElement(property.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteElement(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
