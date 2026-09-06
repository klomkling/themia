using System.Data.Common;

namespace Themia.Audit;

/// <summary>
/// Append-only persistence for <see cref="AuditEntry"/> rows: insert, read, purge. No update, no
/// delete-by-id (design §5). Every method takes its own connection — the store never opens one
/// (design §7): leg 3 writes inside the caller's transaction, so an audit row recording a change that
/// was rolled back can never survive it.
/// </summary>
public interface IAuditStore
{
    /// <summary>
    /// Validates and normalizes <paramref name="entry"/>, inserts it, and returns the generated
    /// <c>bigint</c> row id. Runs inside <paramref name="transaction"/> when supplied, so the write
    /// participates in the caller's atomic unit of work; passing <see langword="null"/> writes
    /// unenlisted, on whatever transaction (if any) <paramref name="connection"/> is already inside.
    /// </summary>
    Task<long> WriteAsync(AuditEntry entry, DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken);

    /// <summary>Returns a filtered, paged page of audit events plus the total matching count.</summary>
    Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, DbConnection connection, CancellationToken cancellationToken);

    /// <summary>Gets a single audit event by its public <see cref="AuditEntry.EventUid"/>, or <see langword="null"/> when none matches.</summary>
    Task<AuditEntry?> GetAsync(Guid eventUid, DbConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every row whose <see cref="AuditEntry.OccurredAt"/> is older than
    /// <paramref name="olderThan"/>, in one statement. On a table that has never been purged this can be
    /// very large: audit never collapses rows and defaults to keep-forever, so a first purge after a year
    /// of traffic holds a long lock — callers should purge in date slices on a big table rather than in
    /// one call. Returns the number of rows removed.
    /// </summary>
    Task<int> PurgeAsync(DateTimeOffset olderThan, DbConnection connection, CancellationToken cancellationToken);
}
