using System.Data.Common;

namespace Themia.Framework.Data.Sequences;

/// <summary>
/// Allocates sequential numeric values inside the caller's open <see cref="DbTransaction"/>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="ISequenceProvider"/>, which allocates on its own independent transaction so that
/// the number survives caller rollback (normal gaps, no duplicates), this allocator operates directly
/// inside the caller's transaction.
/// </para>
/// <para>
/// If the caller's transaction commits, the counter increment commits; if the caller rolls back, the
/// counter increment rolls back with it, leaving NO gap. Concurrent callers on the same sequence key are
/// serialized by the database row lock until each transaction commits or rolls back.
/// </para>
/// <para>
/// Ideal for strict regulatory sequences (such as Thai tax invoices, receipts, withholding tax certificates,
/// and credit notes) where document numbers must form an unbroken consecutive run without gaps.
/// </para>
/// <para>
/// <b>Shared table warning:</b> The allocator and <see cref="ISequenceProvider"/> share the same
/// <c>themia_sequences</c> backing table. Never call <see cref="ISequenceProvider.NextAsync"/> or
/// <see cref="ISequenceProvider.NextHostAsync"/> on a sequence key intended for gapless transactional
/// allocation; doing so allocates on an independent transaction and will break the gapless run.
/// Explicit pre-seeding via <see cref="ISequenceProvider.EnsureSequenceAsync"/> or
/// <see cref="ISequenceProvider.EnsureHostSequenceAsync"/> (e.g. to seed an initial start value or continue an
/// existing number run) IS supported and safe.
/// </para>
/// <para>
/// <b>Database &amp; connection:</b> The allocator ignores <see cref="SequenceOptions.ConnectionString"/> and executes
/// directly on <see cref="DbTransaction.Connection"/>. The <c>themia_sequences</c> table must exist in the caller's
/// database (run <c>SequencesSchemaMigration</c> there), <see cref="SequenceOptions.Engine"/> must match that connection's
/// database engine, and the unqualified table name resolves via the connection's <c>search_path</c> or default schema.
/// </para>
/// <para>
/// <b>Transaction isolation:</b> The caller's transaction must use <see cref="System.Data.IsolationLevel.ReadCommitted"/>.
/// Under <c>RepeatableRead</c> or <c>Serializable</c> on PostgreSQL, queued concurrent callers on the same key will fail
/// with SQLSTATE 40001 (serialization failure) when the lock holder commits. On MySQL, the default <c>REPEATABLE READ</c>
/// risks gap-lock deadlocks during first-use seeding.
/// </para>
/// </remarks>
public interface ITransactionalSequenceAllocator
{
    /// <summary>
    /// Allocates the next value for the CURRENT tenant inside <paramref name="tx"/>.
    /// </summary>
    /// <param name="tx">The caller's active transaction. Must be attached to an open connection. The allocator never commits or rolls it back.</param>
    /// <param name="sequenceKey">Caller-defined key, conventionally colon-namespaced (<c>DocNo:TaxInvoice:2026-10</c>). Max 100 characters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tx"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sequenceKey"/> is null, empty, or exceeds the 100-character column limit.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="tx"/> has no connection, or there is no ambient tenant, or the sequence is exhausted.</exception>
    Task<long> NextInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default);

    /// <summary>
    /// Allocates the next HOST-LEVEL value (outside any tenant) inside <paramref name="tx"/>.
    /// </summary>
    /// <param name="tx">The caller's active transaction. Must be attached to an open connection. The allocator never commits or rolls it back.</param>
    /// <param name="sequenceKey">Caller-defined key, conventionally colon-namespaced (<c>DocNo:TaxInvoice:2026-10</c>). Max 100 characters.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tx"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="sequenceKey"/> is null, empty, or exceeds the 100-character column limit.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="tx"/> has no connection, or the sequence is exhausted.</exception>
    Task<long> NextHostInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default);
}
