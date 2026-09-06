namespace Themia.Audit;

/// <summary>
/// Records an <see cref="AuditEntry"/>: stamps <see cref="AuditEntry.OccurredAt"/> when the caller left
/// it default, validates and normalizes the entry, serializes an optional payload to JSON and redacts
/// it, then writes through <see cref="IAuditStore"/>. See design §7 (two recorders, one interface) and
/// §8 (redaction on the single write path).
/// </summary>
public interface IAuditRecorder
{
    /// <summary>
    /// Records <paramref name="entry"/>, opening its own connection via the registered
    /// <see cref="IAuditDialect"/>. Used by any caller with no ambient unit of work to enlist in — a
    /// worker host, or a consumer using <c>Themia.Audit</c> without the module layer.
    /// </summary>
    /// <param name="entry">The event to record.</param>
    /// <param name="payload">
    /// An optional payload object, serialized with <see cref="System.Text.Json.JsonSerializer"/> and
    /// redacted before storage. <see langword="null"/> leaves <see cref="AuditEntry.Data"/>
    /// <see langword="null"/> rather than storing an empty JSON object.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The recorded entry's <see cref="AuditEntry.EventUid"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="entry"/> fails <see cref="AuditEntry.Validate"/>.</exception>
    ValueTask<Guid> RecordAsync(AuditEntry entry, object? payload = null, CancellationToken cancellationToken = default);
}
