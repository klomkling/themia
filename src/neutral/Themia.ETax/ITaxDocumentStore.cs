using System.Data.Common;

namespace Themia.ETax;

/// <summary>What a failed attempt does with the lease it ran under.</summary>
public enum TaxDocumentLeaseHandling
{
    /// <summary>Clear the lease now: nothing can still be running under it.</summary>
    Release = 0,

    /// <summary>
    /// Leave the lease to lapse on its own. Used after a send timed out: a mailer that ignored its token may still be delivering, and the
    /// lease is what keeps <see cref="ITaxDocumentStore.CancelForReplacementAsync"/> out until that can no longer matter.
    /// </summary>
    KeepUntilExpiry = 1,
}

/// <summary>What <see cref="ITaxDocumentStore.CancelForReplacementAsync"/> did.</summary>
public enum TaxDocumentCancelOutcome
{
    /// <summary>The document is now <see cref="TaxDocumentState.Cancelled"/> and points at its replacement.</summary>
    Cancelled = 0,

    /// <summary>No such document for this seller.</summary>
    NotFound = 1,

    /// <summary>A worker holds a live lease on it; cancelling now could let it be sent as ORIGINAL. Retry later.</summary>
    LeaseHeld = 2,

    /// <summary>It is already cancelled.</summary>
    AlreadyCancelled = 3,
}

/// <summary>
/// The persistence port every app implements over its own tables. Every state change is ONE conditional write on the
/// current state and, where a lease is involved, the claim token: the method returns <see langword="false"/> (and changes
/// nothing) when the condition no longer holds, so two workers, or a worker and a cancel, can never both win.
/// Publish-time behaviour is checked by <c>Themia.ETax.Testing</c>'s conformance class: inherit it and supply a factory.
/// </summary>
/// <remarks>
/// <b>Seller scope, fail closed.</b> Anything an app can expose to a user by document id (<see cref="GetAsync"/>,
/// <see cref="RequeueAsync"/>, <see cref="UpdateBuyerEmailAsync"/>) takes the caller's <c>sellerId</c> and looks the document up by
/// (id, seller): a document of another seller answers exactly like one that does not exist, with no hint that the id is real.
/// This is a second lock; the app still authorizes the caller first.
/// <para>
/// Methods that take a <see cref="DbTransaction"/> run inside the caller's issue transaction and must see its uncommitted
/// writes. Time always arrives as a parameter; the store never reads a clock. Nothing here may log row contents.
/// </para>
/// </remarks>
public interface ITaxDocumentStore
{
    /// <summary>
    /// Returns the document already issued under <paramref name="key"/>, or <see langword="null"/>. Concurrent callers for
    /// the SAME key must be serialised until their transaction ends (e.g. a transaction-scoped advisory lock), so that two
    /// racing issuers cannot both see "none" and both consume a number.
    /// On PostgreSQL take the advisory transaction lock (<c>pg_advisory_xact_lock</c>) and run the SELECT as SEPARATE statements under
    /// READ COMMITTED: a single statement keeps the snapshot it took before it waited for the lock, and so misses the row the lock
    /// holder has just committed.
    /// </summary>
    /// <param name="tx">The caller's issue transaction.</param>
    /// <param name="key">The idempotency key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TaxDocumentRecord?> FindByKeyForIssueAsync(DbTransaction tx, TaxDocumentKey key, CancellationToken ct = default);

    /// <summary>
    /// Reads a document of <paramref name="sellerId"/>. <paramref name="tx"/> is <see langword="null"/> outside an issue transaction.
    /// A document of another seller is <see langword="null"/>, indistinguishable from a missing one.
    /// </summary>
    /// <param name="tx">The caller's transaction when reading inside one.</param>
    /// <param name="id">The document id.</param>
    /// <param name="sellerId">The caller's seller.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TaxDocumentRecord?> GetAsync(DbTransaction? tx, Guid id, string sellerId, CancellationToken ct = default);

    /// <summary>Inserts a new document in state <see cref="TaxDocumentState.Issued"/>, in the caller's transaction.</summary>
    /// <param name="tx">The caller's issue transaction.</param>
    /// <param name="document">The row to insert.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InsertAsync(DbTransaction tx, NewTaxDocument document, CancellationToken ct = default);

    /// <summary>
    /// Moves a document of <paramref name="sellerId"/> to <see cref="TaxDocumentState.Cancelled"/>, recording
    /// <paramref name="replacedByDocumentId"/>, from any state except Cancelled. Refuses while a live lease is held
    /// (<see cref="TaxDocumentCancelOutcome.LeaseHeld"/>) so a cancelled document is never sent as ORIGINAL.
    /// </summary>
    /// <param name="tx">The caller's issue transaction.</param>
    /// <param name="documentId">The document being replaced.</param>
    /// <param name="replacedByDocumentId">The replacement's id (it is inserted later in the same transaction).</param>
    /// <param name="sellerId">The replacement's seller; a document of another seller is <see cref="TaxDocumentCancelOutcome.NotFound"/>.</param>
    /// <param name="now">The current time, for the lease check.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TaxDocumentCancelOutcome> CancelForReplacementAsync(
        DbTransaction tx, Guid documentId, Guid replacedByDocumentId, string sellerId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Leases up to <paramref name="max"/> documents that are due: state <see cref="TaxDocumentState.Issued"/> or
    /// <see cref="TaxDocumentState.OriginalPrepared"/>, no live lease, and <c>NextAttemptAt</c> unset or not after
    /// <paramref name="now"/>. Sets the claim token and expiry (<paramref name="now"/> + <paramref name="lease"/>) and returns them.
    /// An expired lease is claimable again: this is also the sweeper.
    /// </summary>
    /// <param name="max">The most to claim.</param>
    /// <param name="now">The current time.</param>
    /// <param name="lease">How long the lease lasts.</param>
    /// <param name="claimToken">The token to stamp on every claimed row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TaxDocumentRecord>> ClaimDueAsync(int max, DateTimeOffset now, TimeSpan lease, Guid claimToken, CancellationToken ct = default);

    /// <summary>
    /// Records a rendered ORIGINAL: appends it to the evidence history and moves the document to
    /// <see cref="TaxDocumentState.OriginalPrepared"/>. Conditional on state Issued/OriginalPrepared AND a live lease
    /// held by <paramref name="claimToken"/>. <c>OriginalRenderedAt</c> and <c>PdfSha256</c> are set only if still unset.
    /// </summary>
    /// <returns><see langword="false"/> when the condition no longer holds (cancelled, or the lease was lost).</returns>
    Task<bool> RecordOriginalAsync(
        Guid id, Guid claimToken, DateTimeOffset now, string pdfSha256, string rendererVersion, CancellationToken ct = default);

    /// <summary>Every ORIGINAL render recorded for the document, oldest first.</summary>
    /// <param name="id">The document id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TaxDocumentRenderEvidence>> GetEvidenceAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// The re-check immediately before sending: conditional on state <see cref="TaxDocumentState.OriginalPrepared"/> AND a live
    /// lease held by <paramref name="claimToken"/>; on success extends the lease to <paramref name="now"/> + <paramref name="lease"/>
    /// so a cancel cannot slip in while the email goes out.
    /// </summary>
    /// <returns><see langword="false"/> when the document was cancelled or the lease lapsed: do NOT send.</returns>
    Task<bool> BeginSendAsync(Guid id, Guid claimToken, DateTimeOffset now, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Marks the document <see cref="TaxDocumentState.Sent"/> and clears the lease. Conditional on state OriginalPrepared and the token.</summary>
    Task<bool> MarkSentAsync(Guid id, Guid claimToken, DateTimeOffset now, string messageId, CancellationToken ct = default);

    /// <summary>
    /// Ends a failed attempt that will be retried: keeps the state, stores the attempt count, the error code and
    /// <paramref name="nextAttemptAt"/>, and clears the lease unless <paramref name="leaseHandling"/> is
    /// <see cref="TaxDocumentLeaseHandling.KeepUntilExpiry"/> (then the token and expiry are left untouched). Conditional on state
    /// Issued/OriginalPrepared and the token.
    /// </summary>
    Task<bool> ReleaseAsync(
        Guid id, Guid claimToken, int attempts, DateTimeOffset nextAttemptAt, string errorCode,
        TaxDocumentLeaseHandling leaseHandling = TaxDocumentLeaseHandling.Release, CancellationToken ct = default);

    /// <summary>
    /// Moves the document to <see cref="TaxDocumentState.Failed"/>, clearing the lease unless <paramref name="leaseHandling"/> is
    /// <see cref="TaxDocumentLeaseHandling.KeepUntilExpiry"/>. A kept lease still blocks <see cref="CancelForReplacementAsync"/> until it lapses.
    /// Conditional on state Issued/OriginalPrepared and the token.
    /// </summary>
    Task<bool> FailAsync(
        Guid id, Guid claimToken, int attempts, string errorCode,
        TaxDocumentLeaseHandling leaseHandling = TaxDocumentLeaseHandling.Release, CancellationToken ct = default);

    /// <summary>
    /// Returns a <see cref="TaxDocumentState.Failed"/> document to automation: <see cref="TaxDocumentState.OriginalPrepared"/> when an
    /// ORIGINAL was already rendered, otherwise <see cref="TaxDocumentState.Issued"/>; resets attempts, backoff and the error code, and records
    /// who and when. Conditional on state Failed.
    /// </summary>
    /// <param name="id">The document id.</param>
    /// <param name="sellerId">The caller's seller; a document of another seller returns <see langword="false"/> like a missing one.</param>
    /// <param name="requestedBy">An opaque id of the actor (never a name or email).</param>
    /// <param name="now">The current time.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> RequeueAsync(Guid id, string sellerId, string requestedBy, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Corrects the recipient while the document is neither Sent nor Cancelled. Returns <see langword="false"/> otherwise.</summary>
    /// <param name="id">The document id.</param>
    /// <param name="sellerId">The caller's seller; a document of another seller returns <see langword="false"/> like a missing one.</param>
    /// <param name="buyerEmail">The new address.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> UpdateBuyerEmailAsync(Guid id, string sellerId, string buyerEmail, CancellationToken ct = default);
}
