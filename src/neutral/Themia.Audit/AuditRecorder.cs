using System.Data.Common;
using System.Text.Json;
using Themia.Audit.Redaction;

namespace Themia.Audit;

/// <summary>
/// Default <see cref="IAuditRecorder"/>: the single write path every leg of <c>Themia.Audit</c> funnels
/// through (design §7/§8). <see cref="RecordAsync"/> opens its own connection for a caller with no unit
/// of work of its own; <see cref="RecordOnAsync"/> writes on a connection the caller already owns, so
/// <c>Themia.Modules.Audit</c>'s <c>TransactionalAuditRecorder</c> can enlist the write in the caller's
/// ambient transaction. Both funnel through one private method — redaction lives there, and nowhere
/// else, so no route to <see cref="IAuditStore"/> can ship unredacted.
/// </summary>
public sealed class AuditRecorder : IAuditRecorder
{
    private readonly IAuditStore store;
    private readonly IAuditRedactor redactor;
    private readonly TimeProvider timeProvider;
    private readonly IAuditDialect dialect;
    private readonly AuditOptions options;

    /// <summary>Creates the recorder.</summary>
    /// <param name="store">The append-only store every write goes through.</param>
    /// <param name="redactor">Redacts the serialized payload before it is stored.</param>
    /// <param name="timeProvider">Supplies <see cref="AuditEntry.OccurredAt"/> when the caller left it unset.</param>
    /// <param name="dialect">Opens the connection <see cref="RecordAsync"/> writes on.</param>
    /// <param name="options">Supplies the connection string <paramref name="dialect"/> opens against.</param>
    public AuditRecorder(IAuditStore store, IAuditRedactor redactor, TimeProvider timeProvider, IAuditDialect dialect, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);

        this.store = store;
        this.redactor = redactor;
        this.timeProvider = timeProvider;
        this.dialect = dialect;
        this.options = options;
    }

    /// <inheritdoc />
    public async ValueTask<Guid> RecordAsync(AuditEntry entry, object? payload = null, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await RecordCoreAsync(entry, payload, connection, transaction: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records <paramref name="entry"/> on a connection the caller already owns, enlisting in
    /// <paramref name="transaction"/> when supplied. Never opens a connection itself — called by
    /// <c>Themia.Modules.Audit</c>'s <c>TransactionalAuditRecorder</c> (a different assembly, hence
    /// public) to write inside a transaction the caller already owns.
    /// </summary>
    /// <param name="entry">The event to record.</param>
    /// <param name="payload">
    /// An optional payload object, serialized and redacted before storage; <see langword="null"/> leaves
    /// <see cref="AuditEntry.Data"/> <see langword="null"/>.
    /// </param>
    /// <param name="connection">The connection to write on. Must already be open.</param>
    /// <param name="transaction">The transaction to enlist in, or <see langword="null"/> to write unenlisted.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The recorded entry's <see cref="AuditEntry.EventUid"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="entry"/> fails <see cref="AuditEntry.Validate"/>.</exception>
    public ValueTask<Guid> RecordOnAsync(AuditEntry entry, object? payload, DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return RecordCoreAsync(entry, payload, connection, transaction, cancellationToken);
    }

    // The single write path (design §7/§8): stamp OccurredAt, validate, normalize, serialize the
    // payload, redact it, then store. Both public entry points call only this — a second path to
    // IAuditStore would let one of them ship unredacted.
    private async ValueTask<Guid> RecordCoreAsync(
        AuditEntry entry, object? payload, DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var stamped = entry.OccurredAt == default
            ? entry with { OccurredAt = timeProvider.GetUtcNow() }
            : entry;

        stamped.Validate();
        var normalized = stamped.Normalize();

        var data = payload is null ? null : redactor.Redact(JsonSerializer.Serialize(payload));
        var withData = normalized with { Data = data };

        await store.WriteAsync(withData, connection, transaction, cancellationToken).ConfigureAwait(false);
        return withData.EventUid;
    }
}
