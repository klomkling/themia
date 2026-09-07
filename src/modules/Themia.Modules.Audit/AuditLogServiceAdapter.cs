using Themia.Audit;
using Themia.Services.Abstractions;

namespace Themia.Modules.Audit;

/// <summary>
/// Implements the dead <c>IAuditLogService</c> seam (design §12) by mapping <see cref="AuditEvent"/> onto
/// <see cref="AuditEntry"/> and recording it through <see cref="IAuditRecorder"/>.
/// <see cref="AuditEvent"/> is not modified — it lives in <c>Themia.Services</c> (framework) while
/// <see cref="AuditEntry"/> lives in <c>Themia.Audit</c> (neutral), and a neutral core cannot reference a
/// framework package. This adapter is the compatibility surface; <see cref="IAuditRecorder"/> is the real
/// one — adopters wanting an outcome other than <see cref="AuditOutcome.Success"/>, a reason, an IP
/// address, or a null actor should use it directly instead.
/// </summary>
public sealed class AuditLogServiceAdapter : IAuditLogService
{
    private readonly IAuditRecorder recorder;

    /// <summary>Creates the adapter over <paramref name="recorder"/>.</summary>
    public AuditLogServiceAdapter(IAuditRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        this.recorder = recorder;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Maps onto <see cref="AuditCategory.Activity"/> with <see cref="AuditOutcome.Success"/> — the only
    /// outcome <see cref="AuditEvent"/> can express, since its <c>ActorId</c>/<c>EntityId</c>/<c>EntityType</c>
    /// are non-nullable strings. <see cref="AuditEvent.Metadata"/> is passed through as the payload
    /// object, so it is serialized and redacted the same way every other payload is.
    /// </remarks>
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        var entry = new AuditEntry
        {
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
            EventType = auditEvent.EventType,
            ActorId = auditEvent.ActorId,
            ActorName = auditEvent.ActorName,
            EntityId = auditEvent.EntityId,
            EntityType = auditEvent.EntityType,
            OccurredAt = auditEvent.OccurredAtUtc,
        };

        await recorder.RecordAsync(entry, auditEvent.Metadata, cancellationToken).ConfigureAwait(false);
    }
}
