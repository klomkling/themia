namespace Themia.Audit.Redaction;

/// <summary>
/// Redacts sensitive property values from a JSON payload before it is stored in
/// <see cref="AuditEntry.Data"/>. Applied unconditionally, on the single write path — not an option, not
/// a helper a caller may forget.
/// </summary>
public interface IAuditRedactor
{
    /// <summary>
    /// Returns a copy of <paramref name="json"/> with the value of every property whose name matches a
    /// configured pattern replaced by the literal string <c>"[redacted]"</c>, at any depth — inside
    /// nested objects and arrays of objects. The property name itself is preserved: that a sensitive
    /// field was present is itself audit-relevant.
    /// </summary>
    /// <param name="json">A well-formed JSON document.</param>
    /// <returns>The redacted JSON document.</returns>
    string Redact(string json);
}
