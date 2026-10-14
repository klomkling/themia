using System.Data;
using System.Data.Common;
using Themia.ETax.Testing;

namespace Themia.ETax.Tests;

/// <summary>
/// A test double of the app-owned store: every operation is atomic under one lock, an issue transaction can be rolled
/// back, and racing issuers of one key are serialised until their transaction ends. It runs the shared conformance class.
/// </summary>
internal sealed class InMemoryTaxDocumentStore : ITaxDocumentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TaxDocumentRecord> _rows = [];
    private readonly Dictionary<Guid, List<TaxDocumentRenderEvidence>> _evidence = [];
    private readonly Dictionary<string, SemaphoreSlim> _keyLocks = [];

    /// <summary>How many times <see cref="InsertAsync"/> succeeded in a transaction that was later rolled back or never ended; for tests.</summary>
    public IReadOnlyCollection<TaxDocumentRecord> Rows
    {
        get { lock (_gate) { return _rows.Values.ToList(); } }
    }

    /// <summary>Rewrites a stored row behind the store's back, to simulate corruption.</summary>
    public void Replace(Guid id, Func<TaxDocumentRecord, TaxDocumentRecord> change)
    {
        lock (_gate) { _rows[id] = change(_rows[id]); }
    }

    public Task<TaxDocumentRecord?> FindByKeyForIssueAsync(DbTransaction tx, TaxDocumentKey key, CancellationToken ct = default)
    {
        var fake = (FakeTransaction)tx;
        var id = $"{key.Kind}|{key.SourceEventId}|{key.Revision}";
        SemaphoreSlim keyLock;
        lock (_gate)
        {
            if (!_keyLocks.TryGetValue(id, out keyLock!))
                _keyLocks[id] = keyLock = new SemaphoreSlim(1, 1);
        }

        return FindAsync(fake, keyLock, key, ct);
    }

    private async Task<TaxDocumentRecord?> FindAsync(FakeTransaction tx, SemaphoreSlim keyLock, TaxDocumentKey key, CancellationToken ct)
    {
        if (!tx.HoldsLock(keyLock))
        {
            await keyLock.WaitAsync(ct).ConfigureAwait(false);
            tx.OnEnd(() => keyLock.Release());
            tx.Hold(keyLock);
        }

        lock (_gate)
        {
            return _rows.Values.FirstOrDefault(r => r.Kind == key.Kind && r.SourceEventId == key.SourceEventId && r.Revision == key.Revision);
        }
    }

    public Task<TaxDocumentRecord?> GetAsync(DbTransaction? tx, Guid id, string sellerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.GetValueOrDefault(id);
            return Task.FromResult(row is not null && row.SellerId == sellerId ? row : null);
        }
    }

    public Task InsertAsync(DbTransaction tx, NewTaxDocument document, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_rows.Values.Any(r => r.Kind == document.Key.Kind && r.SourceEventId == document.Key.SourceEventId && r.Revision == document.Key.Revision))
                throw new InvalidOperationException("duplicate key");

            _rows[document.Id] = new TaxDocumentRecord
            {
                Id = document.Id,
                Kind = document.Key.Kind,
                SourceEventId = document.Key.SourceEventId,
                Revision = document.Key.Revision,
                SellerId = document.SellerId,
                DocumentNumber = document.DocumentNumber,
                IssueDate = document.IssueDate,
                SnapshotJson = document.SnapshotJson,
                SnapshotSchemaVersion = document.SnapshotSchemaVersion,
                SnapshotSha256 = document.SnapshotSha256,
                TemplateVersion = document.TemplateVersion,
                State = TaxDocumentState.Issued,
                BuyerEmail = document.BuyerEmail,
                CreatedAt = document.CreatedAt,
            };
            var id = document.Id;
            ((FakeTransaction)tx).OnRollback(() => { lock (_gate) { _rows.Remove(id); } });
        }

        return Task.CompletedTask;
    }

    public Task<TaxDocumentCancelOutcome> CancelForReplacementAsync(
        DbTransaction tx, Guid documentId, Guid replacedByDocumentId, string sellerId, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(documentId, out var row) || row.SellerId != sellerId)
                return Task.FromResult(TaxDocumentCancelOutcome.NotFound);
            if (row.State == TaxDocumentState.Cancelled)
                return Task.FromResult(TaxDocumentCancelOutcome.AlreadyCancelled);
            if (row.HasLiveLease(now))
                return Task.FromResult(TaxDocumentCancelOutcome.LeaseHeld);

            _rows[documentId] = row with
            {
                State = TaxDocumentState.Cancelled,
                ReplacedByDocumentId = replacedByDocumentId,
                ClaimToken = null,
                ClaimExpiresAt = null,
            };
            ((FakeTransaction)tx).OnRollback(() => { lock (_gate) { _rows[documentId] = row; } });
            return Task.FromResult(TaxDocumentCancelOutcome.Cancelled);
        }
    }

    public Task<IReadOnlyList<TaxDocumentRecord>> ClaimDueAsync(
        int max, DateTimeOffset now, TimeSpan lease, Guid claimToken, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var due = _rows.Values
                .Where(r => r.State is TaxDocumentState.Issued or TaxDocumentState.OriginalPrepared
                    && !r.HasLiveLease(now)
                    && (r.NextAttemptAt is null || r.NextAttemptAt <= now))
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.DocumentNumber)
                .Take(max).ToList();
            var claimed = new List<TaxDocumentRecord>();
            foreach (var row in due)
            {
                var updated = row with { ClaimToken = claimToken, ClaimExpiresAt = now + lease };
                _rows[row.Id] = updated;
                claimed.Add(updated);
            }

            return Task.FromResult<IReadOnlyList<TaxDocumentRecord>>(claimed);
        }
    }

    public Task<bool> RecordOriginalAsync(
        Guid id, Guid claimToken, DateTimeOffset now, string pdfSha256, string rendererVersion, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row)
                || row.State is not (TaxDocumentState.Issued or TaxDocumentState.OriginalPrepared)
                || row.ClaimToken != claimToken || !row.HasLiveLease(now))
                return Task.FromResult(false);

            _rows[id] = row with
            {
                State = TaxDocumentState.OriginalPrepared,
                OriginalRenderedAt = row.OriginalRenderedAt ?? now,
                PdfSha256 = row.PdfSha256 ?? pdfSha256,
            };
            if (!_evidence.TryGetValue(id, out var list))
                _evidence[id] = list = [];
            list.Add(new TaxDocumentRenderEvidence(pdfSha256, rendererVersion));
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<TaxDocumentRenderEvidence>> GetEvidenceAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TaxDocumentRenderEvidence>>(_evidence.TryGetValue(id, out var list) ? list.ToList() : []);
        }
    }

    public Task<bool> BeginSendAsync(Guid id, Guid claimToken, DateTimeOffset now, TimeSpan lease, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row)
                || row.State != TaxDocumentState.OriginalPrepared || row.ClaimToken != claimToken || !row.HasLiveLease(now))
                return Task.FromResult(false);

            _rows[id] = row with { ClaimExpiresAt = now + lease };
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkSentAsync(Guid id, Guid claimToken, DateTimeOffset now, string messageId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row) || row.State != TaxDocumentState.OriginalPrepared || row.ClaimToken != claimToken)
                return Task.FromResult(false);

            _rows[id] = row with { State = TaxDocumentState.Sent, SentAt = now, MessageId = messageId, ClaimToken = null, ClaimExpiresAt = null };
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseAsync(
        Guid id, Guid claimToken, int attempts, DateTimeOffset nextAttemptAt, string errorCode,
        TaxDocumentLeaseHandling leaseHandling = TaxDocumentLeaseHandling.Release, CancellationToken ct = default) =>
        Update(id, claimToken, r => WithLease(r with
        {
            Attempts = attempts,
            NextAttemptAt = nextAttemptAt,
            LastErrorCode = errorCode,
        }, leaseHandling));

    public Task<bool> FailAsync(
        Guid id, Guid claimToken, int attempts, string errorCode,
        TaxDocumentLeaseHandling leaseHandling = TaxDocumentLeaseHandling.Release, CancellationToken ct = default) =>
        Update(id, claimToken, r => WithLease(r with
        {
            State = TaxDocumentState.Failed,
            Attempts = attempts,
            LastErrorCode = errorCode,
        }, leaseHandling));

    private static TaxDocumentRecord WithLease(TaxDocumentRecord row, TaxDocumentLeaseHandling handling) =>
        handling == TaxDocumentLeaseHandling.KeepUntilExpiry ? row : row with { ClaimToken = null, ClaimExpiresAt = null };

    public Task<bool> RequeueAsync(Guid id, string sellerId, string requestedBy, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row) || row.SellerId != sellerId || row.State != TaxDocumentState.Failed)
                return Task.FromResult(false);

            _rows[id] = row with
            {
                State = row.OriginalRenderedAt is null ? TaxDocumentState.Issued : TaxDocumentState.OriginalPrepared,
                Attempts = 0,
                NextAttemptAt = null,
                LastErrorCode = null,
            };
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpdateBuyerEmailAsync(Guid id, string sellerId, string buyerEmail, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row) || row.SellerId != sellerId || row.State is TaxDocumentState.Sent or TaxDocumentState.Cancelled)
                return Task.FromResult(false);

            _rows[id] = row with { BuyerEmail = buyerEmail };
            return Task.FromResult(true);
        }
    }

    private Task<bool> Update(Guid id, Guid claimToken, Func<TaxDocumentRecord, TaxDocumentRecord> change)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row)
                || row.State is not (TaxDocumentState.Issued or TaxDocumentState.OriginalPrepared) || row.ClaimToken != claimToken)
                return Task.FromResult(false);

            _rows[id] = change(row);
            return Task.FromResult(true);
        }
    }

    /// <summary>A minimal transaction: runs undo actions on rollback and frees held key locks when it ends.</summary>
    internal sealed class FakeTransaction : DbTransaction
    {
        private readonly List<Action> _undo = [];
        private readonly List<Action> _onEnd = [];
        private readonly List<SemaphoreSlim> _held = [];
        private bool _ended;

        protected override DbConnection? DbConnection => null;

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        public void OnRollback(Action undo) => _undo.Add(undo);

        public void OnEnd(Action action) => _onEnd.Add(action);

        public void Hold(SemaphoreSlim keyLock) => _held.Add(keyLock);

        public bool HoldsLock(SemaphoreSlim keyLock) => _held.Contains(keyLock);

        public override void Commit() => End(rollback: false);

        public override void Rollback() => End(rollback: true);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                End(rollback: true);
            base.Dispose(disposing);
        }

        private void End(bool rollback)
        {
            if (_ended)
                return;
            _ended = true;
            if (rollback)
                for (var i = _undo.Count - 1; i >= 0; i--)
                    _undo[i]();
            foreach (var a in _onEnd)
                a();
        }
    }

    internal sealed class Harness : ITaxDocumentStoreHarness
    {
        public InMemoryTaxDocumentStore InMemory { get; } = new();

        public ITaxDocumentStore Store => InMemory;

        public Task<DbTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
            Task.FromResult<DbTransaction>(new FakeTransaction());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
