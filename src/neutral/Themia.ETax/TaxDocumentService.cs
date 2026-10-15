using System.Data.Common;
using Microsoft.Extensions.Logging;

namespace Themia.ETax;

/// <summary>
/// Issues documents inside the caller's transaction, renders copies, and requeues failed ones. Logs ids, kinds,
/// states and codes only; snapshot contents (names, tax ids, addresses) never reach a log or an exception message.
/// </summary>
public sealed class TaxDocumentService
{
    private const string PendingNumber = "PENDING";

    private readonly ITaxDocumentStore _store;
    private readonly ITaxDocumentNumberSource _numbers;
    private readonly TaxDocumentRenderer _renderer;
    private readonly TaxDocumentValidationOptions _validation;
    private readonly string _templateVersion;
    private readonly TimeProvider _clock;
    private readonly ILogger<TaxDocumentService> _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="store">The app's store.</param>
    /// <param name="numbers">The gapless number source.</param>
    /// <param name="renderer">Renders copies.</param>
    /// <param name="validation">Validation options for new input.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public TaxDocumentService(
        ITaxDocumentStore store,
        ITaxDocumentNumberSource numbers,
        TaxDocumentRenderer renderer,
        TaxDocumentValidationOptions validation,
        TimeProvider clock,
        ILogger<TaxDocumentService> logger)
        : this(store, numbers, renderer, validation, new TaxDocumentTemplateOptions(), clock, logger)
    {
    }

    /// <summary>Creates the service.</summary>
    /// <param name="store">The app's store.</param>
    /// <param name="numbers">The gapless number source.</param>
    /// <param name="renderer">Renders copies.</param>
    /// <param name="validation">Validation options for new input.</param>
    /// <param name="templates">The adopter's template options; the service reads <see cref="TaxDocumentTemplateOptions.TemplateVersion"/> for new documents.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public TaxDocumentService(
        ITaxDocumentStore store,
        ITaxDocumentNumberSource numbers,
        TaxDocumentRenderer renderer,
        TaxDocumentValidationOptions validation,
        TaxDocumentTemplateOptions templates,
        TimeProvider clock,
        ILogger<TaxDocumentService> logger)
    {
        ArgumentNullException.ThrowIfNull(templates);
        templates.Validate();
        _templateVersion = templates.TemplateVersion;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _numbers = numbers ?? throw new ArgumentNullException(nameof(numbers));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Issues a document in <paramref name="tx"/>: dedupes on the key, validates, checks the note or replacement
    /// preconditions, cancels the replaced document, allocates the number and stores the snapshot. Nothing is committed here.
    /// The transaction must be READ COMMITTED (the number source requires it).
    /// </summary>
    /// <param name="tx">The caller's open transaction.</param>
    /// <param name="request">What to issue.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome. Roll back unless it is <see cref="TaxDocumentIssueOutcome.Issued"/> or <see cref="TaxDocumentIssueOutcome.AlreadyIssued"/>.</returns>
    public async Task<TaxDocumentIssueResult> IssueAsync(DbTransaction tx, IssueTaxDocumentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SellerId);

        // A duplicate is answered BEFORE a number is allocated, so it consumes nothing. The store serialises racing issuers of one key.
        var existing = await _store.FindByKeyForIssueAsync(tx, request.Key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // The key carries no seller. A row of another seller must not leak its id, and must not read as "already issued" to a seller
            // that never issued it.
            return existing.SellerId == request.SellerId
                ? new TaxDocumentIssueResult { Outcome = TaxDocumentIssueOutcome.AlreadyIssued, DocumentId = existing.Id }
                : Failed(TaxDocumentIssueOutcome.SourceConflict);
        }

        var draft = request.Snapshot with { DocumentNumber = PendingNumber };
        var errors = Validate(draft, request.Key);
        if (errors.Count > 0)
            return Failed(TaxDocumentIssueOutcome.ValidationFailed, errors);

        if (draft.Note is { } note && !await NoteReferenceIsValidAsync(tx, note, request.SellerId, ct).ConfigureAwait(false))
            return Failed(TaxDocumentIssueOutcome.NoteReferenceInvalid);

        var id = Guid.NewGuid();
        if (draft.Replacement is { } replacement)
        {
            var replaced = await ReplaceAsync(tx, replacement, request.Key, id, request.SellerId, ct).ConfigureAwait(false);
            if (replaced is not null)
                return replaced;
        }

        var number = await _numbers.NextNumberAsync(tx, request.SellerId, request.Key.Kind, draft.IssueDate, ct).ConfigureAwait(false);
        var snapshot = draft with { DocumentNumber = number };
        errors = Validate(snapshot, request.Key);
        if (errors.Count > 0)
            return Failed(TaxDocumentIssueOutcome.ValidationFailed, errors);

        var json = TaxDocumentSnapshotSerializer.Serialize(snapshot);
        await _store.InsertAsync(tx, new NewTaxDocument
        {
            Id = id,
            Key = request.Key,
            SellerId = request.SellerId,
            DocumentNumber = number,
            IssueDate = snapshot.IssueDate,
            SnapshotJson = json,
            SnapshotSchemaVersion = snapshot.SchemaVersion,
            SnapshotSha256 = TaxDocumentSnapshotSerializer.ComputeHash(json),
            TemplateVersion = _templateVersion,
            BuyerEmail = request.BuyerEmail,
            CreatedAt = _clock.GetUtcNow(),
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("Tax document {DocumentId} ({Kind}) issued.", id, request.Key.Kind);
        return new TaxDocumentIssueResult { Outcome = TaxDocumentIssueOutcome.Issued, DocumentId = id };
    }

    /// <summary>
    /// Renders a stored document as a COPY (or a cancelled copy with the CANCELLED watermark). The ORIGINAL is only ever
    /// rendered by <see cref="TaxDocumentSender"/> on the send path. Downloads are not stored: each call renders again.
    /// </summary>
    /// <param name="id">The document id.</param>
    /// <param name="sellerId">The caller's seller. A document of another seller is reported exactly like a missing one.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The render, or <see langword="null"/> when there is no such document for this seller.</returns>
    /// <exception cref="TaxDocumentSnapshotException">The stored snapshot does not match its hash.</exception>
    public async Task<TaxDocumentRender?> RenderCopyAsync(Guid id, string sellerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);

        var document = await _store.GetAsync(null, id, sellerId, ct).ConfigureAwait(false);
        if (document is null || document.SellerId != sellerId)
            return null;

        var snapshot = TaxDocumentSnapshotSerializer.Deserialize(document.SnapshotJson, document.SnapshotSha256);
        var marker = document.State == TaxDocumentState.Cancelled ? TaxDocumentMarker.CancelledCopy : TaxDocumentMarker.Copy;
        return await _renderer.RenderAsync(snapshot, document.TemplateVersion, marker, ct).ConfigureAwait(false);
    }

    /// <summary>Returns a Failed document to automation (see <see cref="ITaxDocumentStore.RequeueAsync"/>). Logs who and when.</summary>
    /// <param name="id">The document id.</param>
    /// <param name="sellerId">The caller's seller. A document of another seller is reported exactly like a missing one.</param>
    /// <param name="requestedBy">An opaque id of the actor; it is logged, so never a name or an email.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="false"/> when the document is not Failed, or does not exist for this seller.</returns>
    public async Task<bool> RequeueAsync(Guid id, string sellerId, string requestedBy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedBy);

        var requeued = await _store.RequeueAsync(id, sellerId, requestedBy, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        if (requeued)
            _logger.LogInformation("Tax document {DocumentId} requeued by {RequestedBy}.", id, requestedBy);
        return requeued;
    }

    private List<TaxDocumentValidationError> Validate(TaxDocumentSnapshot snapshot, TaxDocumentKey key)
    {
        var errors = TaxDocumentSnapshotValidator.Validate(snapshot, _validation).ToList();
        if (snapshot.Kind != key.Kind)
            errors.Add(new TaxDocumentValidationError(TaxDocumentValidationCode.Malformed, "kind"));
        return errors;
    }

    private async Task<bool> NoteReferenceIsValidAsync(DbTransaction tx, TaxNoteReference note, string sellerId, CancellationToken ct)
    {
        var original = await _store.GetAsync(tx, note.OriginalDocumentId, sellerId, ct).ConfigureAwait(false);
        return original is not null
            && original.SellerId == sellerId
            && original.State != TaxDocumentState.Cancelled
            && original.OriginalRenderedAt is not null
            && original.DocumentNumber == note.OriginalDocumentNumber;
    }

    private async Task<TaxDocumentIssueResult?> ReplaceAsync(
        DbTransaction tx, TaxDocumentReplacement replacement, TaxDocumentKey key, Guid newId, string sellerId, CancellationToken ct)
    {
        var replaced = await _store.GetAsync(tx, replacement.ReplacesDocumentId, sellerId, ct).ConfigureAwait(false);
        if (replaced is null || replaced.SellerId != sellerId || replaced.DocumentNumber != replacement.OriginalDocumentNumber)
            return Failed(TaxDocumentIssueOutcome.ReplacementInvalid);

        // A replacement is the next revision of the SAME source event, never a document for a different event or kind.
        if (replaced.Kind != key.Kind || replaced.SourceEventId != key.SourceEventId || key.Revision != replaced.Revision + 1)
            return Failed(TaxDocumentIssueOutcome.ReplacementInvalid);

        var outcome = await _store.CancelForReplacementAsync(
            tx, replacement.ReplacesDocumentId, newId, sellerId, _clock.GetUtcNow(), ct).ConfigureAwait(false);
        return outcome switch
        {
            TaxDocumentCancelOutcome.Cancelled => null,
            TaxDocumentCancelOutcome.LeaseHeld => Failed(TaxDocumentIssueOutcome.ReplacedDocumentBusy),
            _ => Failed(TaxDocumentIssueOutcome.ReplacementInvalid),
        };
    }

    private static TaxDocumentIssueResult Failed(TaxDocumentIssueOutcome outcome, IReadOnlyList<TaxDocumentValidationError>? errors = null) =>
        new() { Outcome = outcome, Errors = errors ?? [] };
}
