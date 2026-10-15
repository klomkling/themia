using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Themia.ETax.Withholding;

/// <summary>
/// Issues withholding certificates inside the caller's transaction. Logs ids, states and codes only; snapshot contents (names, tax ids,
/// addresses) never reach a log or an exception message.
/// </summary>
public sealed class WithholdingCertificateService
{
    private const string PendingNumber = "PENDING";
    private const string SnapshotField = "snapshot";

    private readonly IWithholdingCertificateStore _store;
    private readonly IWithholdingNumberSource _numbers;
    private readonly IncomeTypeCatalogue _catalogue;
    private readonly WithholdingValidationOptions _validation;
    private readonly TimeProvider _clock;
    private readonly ILogger<WithholdingCertificateService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="store">The app's store.</param>
    /// <param name="numbers">The gapless number source.</param>
    /// <param name="catalogue">The income-type catalogue new certificates are selected from.</param>
    /// <param name="validation">Validation options for new input.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public WithholdingCertificateService(
        IWithholdingCertificateStore store,
        IWithholdingNumberSource numbers,
        IncomeTypeCatalogue catalogue,
        WithholdingValidationOptions validation,
        TimeProvider clock,
        ILogger<WithholdingCertificateService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _numbers = numbers ?? throw new ArgumentNullException(nameof(numbers));
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Issues a certificate in <paramref name="tx"/>: dedupes on the key, validates, checks the replacement preconditions, cancels the
    /// replaced certificate, allocates the number and stores the snapshot. Nothing is committed here. The transaction must be READ COMMITTED
    /// (the number source requires it).
    /// </summary>
    /// <param name="tx">The caller's open transaction.</param>
    /// <param name="request">What to issue.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome. Roll back unless it is <see cref="WithholdingIssueOutcome.Issued"/> or <see cref="WithholdingIssueOutcome.AlreadyIssued"/>.</returns>
    public async Task<WithholdingIssueResult> IssueAsync(DbTransaction tx, IssueWithholdingCertificateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PayerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Key.SourceEventId);

        // A duplicate is answered BEFORE a number is allocated, so it consumes nothing. The store serialises racing issuers of one key.
        var existing = await _store.FindByKeyForIssueAsync(tx, request.Key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // The key carries no payer. A row of another payer must not leak its id, and must not read as "already issued" to a payer
            // that never issued it.
            return existing.PayerId == request.PayerId
                ? new WithholdingIssueResult { Outcome = WithholdingIssueOutcome.AlreadyIssued, DocumentId = existing.Id }
                : Failed(WithholdingIssueOutcome.SourceConflict);
        }

        // Everything below works on a private copy, so what is validated is exactly what is serialized and stored even if the caller's
        // lists change (or are shared with another thread) while this runs.
        if (!TryDetach(request.Snapshot, out var detached))
            return Failed(WithholdingIssueOutcome.ValidationFailed, [new WithholdingValidationError(WithholdingValidationCode.Malformed, SnapshotField)]);

        var draft = detached with { DocumentNumber = PendingNumber };
        var errors = WithholdingSnapshotValidator.Validate(draft, _catalogue, _validation);
        if (errors.Count > 0)
            return Failed(WithholdingIssueOutcome.ValidationFailed, errors);

        var id = Guid.NewGuid();
        var replacementError = await CheckAndCancelReplacedAsync(tx, draft, request, id, ct).ConfigureAwait(false);
        if (replacementError is not null)
            return replacementError;

        var number = await _numbers.NextNumberAsync(tx, request.PayerId, draft.IssueDate, ct).ConfigureAwait(false);
        var snapshot = draft with { DocumentNumber = number };
        errors = WithholdingSnapshotValidator.Validate(snapshot, _catalogue, _validation);
        if (errors.Count > 0)
            return Failed(WithholdingIssueOutcome.ValidationFailed, errors);

        var json = WithholdingSnapshotSerializer.Serialize(snapshot);
        await _store.InsertAsync(tx, new NewWithholdingCertificate
        {
            Id = id,
            Key = request.Key,
            PayerId = request.PayerId,
            DocumentNumber = number,
            IssueDate = snapshot.IssueDate,
            SnapshotJson = json,
            SnapshotSchemaVersion = snapshot.SchemaVersion,
            SnapshotSha256 = WithholdingSnapshotSerializer.ComputeHash(json),
            TemplateVersion = WithholdingTemplates.CurrentVersion,
            CreatedAt = _clock.GetUtcNow(),
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Withholding certificate {DocumentId} issued.", id);
        return new WithholdingIssueResult { Outcome = WithholdingIssueOutcome.Issued, DocumentId = id };
    }

    // The snapshot is written and read back through the one persisted format: the copy shares nothing with the caller's objects.
    private static bool TryDetach(WithholdingCertificateSnapshot snapshot, out WithholdingCertificateSnapshot detached)
    {
        detached = null!;
        if (snapshot is null)
            return false;

        try
        {
            var json = WithholdingSnapshotSerializer.Serialize(snapshot);
            detached = WithholdingSnapshotSerializer.Deserialize(json, WithholdingSnapshotSerializer.ComputeHash(json));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or TaxDocumentSnapshotException)
        {
            // The text of these exceptions can quote the offending value, so it is not logged or returned.
            return false;
        }
    }

    private async Task<WithholdingIssueResult?> CheckAndCancelReplacedAsync(
        DbTransaction tx, WithholdingCertificateSnapshot draft, IssueWithholdingCertificateRequest request, Guid newId, CancellationToken ct)
    {
        var replacement = draft.Replacement;
        var key = request.Key;

        // Revision 1 is the first certificate of an event; every later revision replaces the one before it, and only those.
        if ((replacement is null) != (key.Revision == 1))
            return Failed(WithholdingIssueOutcome.ReplacementInvalid);
        if (replacement is null)
            return null;

        var replaced = await _store.GetAsync(tx, replacement.ReplacesDocumentId, request.PayerId, ct).ConfigureAwait(false);
        if (replaced is null
            || replaced.PayerId != request.PayerId
            || replaced.DocumentNumber != replacement.OriginalDocumentNumber
            || replaced.IssueDate != replacement.OriginalIssueDate
            || replaced.SourceEventId != key.SourceEventId
            || key.Revision != replaced.Revision + 1)
        {
            return Failed(WithholdingIssueOutcome.ReplacementInvalid);
        }

        var outcome = await _store.CancelForReplacementAsync(tx, replaced.Id, newId, request.PayerId, ct).ConfigureAwait(false);
        return outcome == WithholdingCancelOutcome.Cancelled ? null : Failed(WithholdingIssueOutcome.ReplacementInvalid);
    }

    private static WithholdingIssueResult Failed(WithholdingIssueOutcome outcome, IReadOnlyList<WithholdingValidationError>? errors = null) =>
        new() { Outcome = outcome, Errors = errors ?? [] };
}
