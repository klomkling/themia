using System.Data.Common;

namespace Themia.ETax.Withholding;

/// <summary>
/// The app-owned store of certificates (its tables, its engine). Every write joins the caller's issue transaction. Every read that
/// takes a payer is scoped to it: another payer's certificate answers exactly like a missing one (fail closed). Run
/// <c>Themia.ETax.Withholding.Testing</c> against your implementation.
/// </summary>
public interface IWithholdingCertificateStore
{
    /// <summary>
    /// Finds the certificate of <paramref name="key"/> and, in the same call, serialises racing issuers of that key until
    /// <paramref name="tx"/> ends (e.g. an advisory lock or <c>SELECT … FOR UPDATE</c> on the key), so two of them cannot both see "none"
    /// and both consume a number.
    /// </summary>
    /// <param name="tx">The caller's issue transaction.</param>
    /// <param name="key">The idempotency key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WithholdingCertificateRecord?> FindByKeyForIssueAsync(DbTransaction tx, WithholdingCertificateKey key, CancellationToken ct = default);

    /// <summary>Reads a certificate by id, scoped to <paramref name="payerId"/>.</summary>
    /// <param name="tx">The caller's transaction to read its own uncommitted writes, or <see langword="null"/> to read committed data.</param>
    /// <param name="id">The certificate id.</param>
    /// <param name="payerId">The caller's payer.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WithholdingCertificateRecord?> GetAsync(DbTransaction? tx, Guid id, string payerId, CancellationToken ct = default);

    /// <summary>Inserts a certificate in state Issued. Throws if the key already exists.</summary>
    /// <param name="tx">The caller's issue transaction.</param>
    /// <param name="document">The certificate.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InsertAsync(DbTransaction tx, NewWithholdingCertificate document, CancellationToken ct = default);

    /// <summary>
    /// Cancels <paramref name="certificateId"/> and records <paramref name="replacedByCertificateId"/> as its replacement, in
    /// <paramref name="tx"/>. Cancelled is final and the replacement is write-once.
    /// </summary>
    /// <param name="tx">The caller's issue transaction.</param>
    /// <param name="certificateId">The certificate to cancel.</param>
    /// <param name="replacedByCertificateId">The id the replacement will be inserted under in the same transaction.</param>
    /// <param name="payerId">The caller's payer.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WithholdingCancelOutcome> CancelForReplacementAsync(
        DbTransaction tx, Guid certificateId, Guid replacedByCertificateId, string payerId, CancellationToken ct = default);
}
