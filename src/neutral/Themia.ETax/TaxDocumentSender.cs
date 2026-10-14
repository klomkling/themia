using Microsoft.Extensions.Logging;

namespace Themia.ETax;

/// <summary>
/// The send path (also the sweeper): claims due documents under a lease, renders the ORIGINAL from the stored snapshot, records
/// its evidence, re-checks that the document is still sendable, sends the email, and records the result. Delivery is at-least-once
/// with exactly one ORIGINAL: a crash after sending lets the lease lapse and the same ORIGINAL is re-rendered and resent.
/// Logs ids, kinds, states and codes only; no snapshot content, address or transport message ever reaches a log or an exception.
/// </summary>
public sealed class TaxDocumentSender
{
    private const string SendTimeoutCode = "send_timeout";

    private readonly ITaxDocumentStore _store;
    private readonly ITaxDocumentMailer _mailer;
    private readonly ITaxDocumentFailureHandler _failureHandler;
    private readonly TaxDocumentRenderer _renderer;
    private readonly TaxDocumentSendOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TaxDocumentSender> _logger;

    /// <summary>Creates the sender.</summary>
    /// <param name="store">The app's store.</param>
    /// <param name="mailer">The app's mail transport.</param>
    /// <param name="failureHandler">Alerting hook.</param>
    /// <param name="renderer">Renders the ORIGINAL.</param>
    /// <param name="options">Send options.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public TaxDocumentSender(
        ITaxDocumentStore store,
        ITaxDocumentMailer mailer,
        ITaxDocumentFailureHandler failureHandler,
        TaxDocumentRenderer renderer,
        TaxDocumentSendOptions options,
        TimeProvider clock,
        ILogger<TaxDocumentSender> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _mailer = mailer ?? throw new ArgumentNullException(nameof(mailer));
        _failureHandler = failureHandler ?? throw new ArgumentNullException(nameof(failureHandler));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
    }

    /// <summary>
    /// Claims and processes one batch of due documents. Call it on a schedule.
    /// An exception from the store itself is logged (type only) and does not count as an attempt: if the store cannot write, it cannot
    /// record an attempt either, so a row whose write always throws is retried every lease with an ERROR until the store is fixed.
    /// </summary>
    /// <param name="ct">Cancellation token. A cancelled run leaves its leases to lapse; nothing is lost.</param>
    /// <returns>How many documents were claimed.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var token = Guid.NewGuid();
        var claimed = await _store.ClaimDueAsync(_options.BatchSize, _clock.GetUtcNow(), _options.Lease, token, ct).ConfigureAwait(false);
        foreach (var document in claimed)
        {
            try
            {
                await ProcessAsync(document, token, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unexpected (the store itself failed). The lease lapses and the document is claimed again; type only, never the message.
                _logger.LogError("Tax document {DocumentId} processing aborted: {ExceptionType}.", document.Id, ex.GetType().Name);
            }
        }

        return claimed.Count;
    }

    private async Task ProcessAsync(TaxDocumentRecord document, Guid token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(document.BuyerEmail))
        {
            await FailAsync(document, token, "no_recipient", ct).ConfigureAwait(false);
            return;
        }

        if (!TaxDocumentTemplates.IsSupported(document.TemplateVersion))
        {
            await FailAsync(document, token, "template_unsupported", ct).ConfigureAwait(false);
            return;
        }

        TaxDocumentSnapshot snapshot;
        try
        {
            snapshot = TaxDocumentSnapshotSerializer.Deserialize(document.SnapshotJson, document.SnapshotSha256);
        }
        catch (TaxDocumentSnapshotException)
        {
            await FailAsync(document, token, "snapshot_integrity", ct).ConfigureAwait(false);
            return;
        }

        var render = await RenderOriginalAsync(document, snapshot, ct).ConfigureAwait(false);
        if (render is null)
        {
            await RetryOrFailAsync(document, token, "render_error", null, ct).ConfigureAwait(false);
            return;
        }

        if (!await RecordEvidenceAsync(document, token, render, ct).ConfigureAwait(false))
            return;

        var sendStartedAt = _clock.GetUtcNow();
        if (!await _store.BeginSendAsync(document.Id, token, sendStartedAt, _options.Lease, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Tax document {DocumentId} is no longer sendable (cancelled or lease lost); not sent.", document.Id);
            return;
        }

        var result = await SendAsync(document, snapshot, render, ct).ConfigureAwait(false);
        switch (result.Outcome)
        {
            case TaxDocumentSendOutcome.Sent:
                await MarkSentAsync(document, token, ct).ConfigureAwait(false);
                break;
            case TaxDocumentSendOutcome.Transient:
                await RetryOrFailAsync(document, token, result.ErrorCode ?? "send_transient", SendTimeoutLeaseEnd(result, sendStartedAt), ct)
                    .ConfigureAwait(false);
                break;
            default:
                await FailAsync(document, token, result.ErrorCode ?? "send_permanent", TaxDocumentLeaseHandling.Release, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task<TaxDocumentRender?> RenderOriginalAsync(TaxDocumentRecord document, TaxDocumentSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            return await _renderer.RenderAsync(snapshot, document.TemplateVersion, TaxDocumentMarker.Original, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Tax document {DocumentId} render failed: {ExceptionType}.", document.Id, ex.GetType().Name);
            return null;
        }
    }

    // True when the ORIGINAL is recorded and sending may go on; false when the document must not be sent (already handled here).
    private async Task<bool> RecordEvidenceAsync(TaxDocumentRecord document, Guid token, TaxDocumentRender render, CancellationToken ct)
    {
        var history = await _store.GetEvidenceAsync(document.Id, ct).ConfigureAwait(false);
        TaxDocumentEvidenceOutcome outcome;
        try
        {
            outcome = TaxDocumentEvidence.Check(history, render);
        }
        catch (TaxDocumentRenderMismatchException)
        {
            await FailAsync(document, token, "render_mismatch", ct).ConfigureAwait(false);
            return false;
        }

        if (outcome == TaxDocumentEvidenceOutcome.NewRendererVersion)
        {
            _logger.LogWarning(
                "Tax document {DocumentId} ORIGINAL re-rendered by a new renderer version {RendererVersion}; recording new evidence.",
                document.Id, render.RendererVersion);
        }

        var recorded = await _store.RecordOriginalAsync(
            document.Id, token, _clock.GetUtcNow(), render.PdfSha256, render.RendererVersion, ct).ConfigureAwait(false);
        if (!recorded)
            _logger.LogInformation("Tax document {DocumentId} is no longer sendable (cancelled or lease lost); ORIGINAL not recorded.", document.Id);
        return recorded;
    }

    private async Task<TaxDocumentSendResult> SendAsync(
        TaxDocumentRecord document, TaxDocumentSnapshot snapshot, TaxDocumentRender render, CancellationToken ct)
    {
        var email = new TaxDocumentEmail
        {
            DocumentId = document.Id,
            DocumentNumber = document.DocumentNumber,
            Kind = document.Kind,
            To = document.BuyerEmail!,
            RevenueDepartmentAddress = _options.RevenueDepartmentAddress,
            Pdf = render.Pdf,
            ReplacesDocumentNumber = snapshot.Replacement?.OriginalDocumentNumber,
        };

        // The send runs under the lease BeginSendAsync just extended, and that lease is all that keeps a cancel out. So the send
        // gets less time than the lease: a mailer that stalls is cancelled (and, if it ignores the token, abandoned) before the
        // lease can lapse.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.SendTimeout);
        Task<TaxDocumentSendResult>? send = null;
        try
        {
            send = _mailer.SendAsync(email, timeout.Token);
            return await send.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Tax document {DocumentId} send exceeded {SendTimeout}.", document.Id, _options.SendTimeout);
            ObserveAbandonedSend(send, document.Id);
            return TaxDocumentSendResult.Transient(SendTimeoutCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A transport exception message can echo the recipient: keep the type only.
            _logger.LogWarning("Tax document {DocumentId} mailer threw {ExceptionType}.", document.Id, ex.GetType().Name);
            return TaxDocumentSendResult.Transient("mailer_exception");
        }
    }

    // A mailer that ignored the token keeps running after we stopped waiting. Observe its end so a fault is never an unobserved task
    // exception, and say so if it finished anyway: a late "Sent" means the email may have gone out after the timeout.
    private void ObserveAbandonedSend(Task<TaxDocumentSendResult>? send, Guid documentId)
    {
        if (send is null)
            return;

        _ = send.ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                    _logger.LogWarning("Tax document {DocumentId} abandoned send later faulted: {ExceptionType}.", documentId, task.Exception!.GetBaseException().GetType().Name);
                else if (task.IsCompletedSuccessfully)
                    _logger.LogWarning("Tax document {DocumentId} abandoned send completed late with {Outcome}; it may have been delivered.", documentId, task.Result.Outcome);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task MarkSentAsync(TaxDocumentRecord document, Guid token, CancellationToken ct)
    {
        var marked = await _store.MarkSentAsync(document.Id, token, _clock.GetUtcNow(), document.Id.ToString(), ct).ConfigureAwait(false);
        if (marked)
            _logger.LogInformation("Tax document {DocumentId} ({Kind}) sent.", document.Id, document.Kind);
        else
            _logger.LogWarning("Tax document {DocumentId} was sent but its lease was lost before it could be marked; it will be resent.", document.Id);
    }

    // After a send timeout the mailer may still be delivering (one that ignored its token). The lease is all that keeps a cancel out, so it
    // is left to lapse on its own instead of being released; null means nothing can still be running under it.
    private DateTimeOffset? SendTimeoutLeaseEnd(TaxDocumentSendResult result, DateTimeOffset sendStartedAt) =>
        result.ErrorCode == SendTimeoutCode ? sendStartedAt + _options.Lease : null;

    private async Task RetryOrFailAsync(
        TaxDocumentRecord document, Guid token, string errorCode, DateTimeOffset? keepLeaseUntil, CancellationToken ct)
    {
        var attempts = document.Attempts + 1;
        var handling = keepLeaseUntil is null ? TaxDocumentLeaseHandling.Release : TaxDocumentLeaseHandling.KeepUntilExpiry;
        if (attempts >= _options.MaxAttempts)
        {
            await FailAsync(document, token, errorCode, handling, ct).ConfigureAwait(false);
            return;
        }

        var delay = TaxDocumentBackoff.Delay(attempts, _options.InitialBackoff, _options.MaxBackoff, Random.Shared.NextDouble());
        var nextAttemptAt = _clock.GetUtcNow() + delay;
        if (keepLeaseUntil is { } leaseEnd && nextAttemptAt < leaseEnd)
            nextAttemptAt = leaseEnd;

        var released = await _store.ReleaseAsync(document.Id, token, attempts, nextAttemptAt, errorCode, handling, ct).ConfigureAwait(false);
        if (released)
            _logger.LogWarning("Tax document {DocumentId} attempt {Attempts} failed ({ErrorCode}); retrying in {Delay}.", document.Id, attempts, errorCode, delay);
    }

    private Task FailAsync(TaxDocumentRecord document, Guid token, string errorCode, CancellationToken ct) =>
        FailAsync(document, token, errorCode, TaxDocumentLeaseHandling.Release, ct);

    private async Task FailAsync(
        TaxDocumentRecord document, Guid token, string errorCode, TaxDocumentLeaseHandling handling, CancellationToken ct)
    {
        var attempts = document.Attempts + 1;
        if (!await _store.FailAsync(document.Id, token, attempts, errorCode, handling, ct).ConfigureAwait(false))
            return;

        // One ERROR per entry into Failed, then the alerting hook.
        _logger.LogError("Tax document {DocumentId} ({Kind}) failed after {Attempts} attempts: {ErrorCode}.", document.Id, document.Kind, attempts, errorCode);
        try
        {
            await _failureHandler.OnFailedAsync(new TaxDocumentFailure(document.Id, document.Kind, errorCode, attempts), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Tax document {DocumentId} failure handler threw {ExceptionType}.", document.Id, ex.GetType().Name);
        }
    }
}
