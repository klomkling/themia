using System.Data;
using System.Data.Common;
using Themia.ETax.Withholding.Testing;

namespace Themia.ETax.Withholding.Tests;

/// <summary>
/// A test double of the app-owned store: every operation is atomic under one lock, an issue transaction can be rolled back, and racing
/// issuers of one key are serialised until their transaction ends. It runs the shared conformance class.
/// </summary>
internal sealed class InMemoryWithholdingCertificateStore : IWithholdingCertificateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, WithholdingCertificateRecord> _rows = [];
    private readonly Dictionary<string, SemaphoreSlim> _keyLocks = [];

    public IReadOnlyCollection<WithholdingCertificateRecord> Rows
    {
        get { lock (_gate) { return _rows.Values.ToList(); } }
    }

    public Task<WithholdingCertificateRecord?> FindByKeyForIssueAsync(DbTransaction tx, WithholdingCertificateKey key, CancellationToken ct = default)
    {
        var fake = (FakeTransaction)tx;
        var id = $"{key.SourceEventId}|{key.Revision}";
        SemaphoreSlim keyLock;
        lock (_gate)
        {
            if (!_keyLocks.TryGetValue(id, out keyLock!))
                _keyLocks[id] = keyLock = new SemaphoreSlim(1, 1);
        }

        return FindAsync(fake, keyLock, key, ct);
    }

    private async Task<WithholdingCertificateRecord?> FindAsync(FakeTransaction tx, SemaphoreSlim keyLock, WithholdingCertificateKey key, CancellationToken ct)
    {
        if (!tx.HoldsLock(keyLock))
        {
            await keyLock.WaitAsync(ct).ConfigureAwait(false);
            tx.OnEnd(() => keyLock.Release());
            tx.Hold(keyLock);
        }

        lock (_gate)
        {
            return _rows.Values.FirstOrDefault(r => r.SourceEventId == key.SourceEventId && r.Revision == key.Revision);
        }
    }

    public Task<WithholdingCertificateRecord?> GetAsync(DbTransaction? tx, Guid id, string payerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.GetValueOrDefault(id);
            return Task.FromResult(row is not null && row.PayerId == payerId ? row : null);
        }
    }

    public Task InsertAsync(DbTransaction tx, NewWithholdingCertificate document, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_rows.Values.Any(r => r.SourceEventId == document.Key.SourceEventId && r.Revision == document.Key.Revision))
                throw new InvalidOperationException("duplicate key");

            _rows[document.Id] = new WithholdingCertificateRecord
            {
                Id = document.Id,
                SourceEventId = document.Key.SourceEventId,
                Revision = document.Key.Revision,
                PayerId = document.PayerId,
                DocumentNumber = document.DocumentNumber,
                IssueDate = document.IssueDate,
                SnapshotJson = document.SnapshotJson,
                SnapshotSchemaVersion = document.SnapshotSchemaVersion,
                SnapshotSha256 = document.SnapshotSha256,
                TemplateVersion = document.TemplateVersion,
                State = WithholdingCertificateState.Issued,
                CreatedAt = document.CreatedAt,
            };
            var id = document.Id;
            ((FakeTransaction)tx).OnRollback(() => { lock (_gate) { _rows.Remove(id); } });
        }

        return Task.CompletedTask;
    }

    public Task<WithholdingCancelOutcome> CancelForReplacementAsync(
        DbTransaction tx, Guid certificateId, Guid replacedByCertificateId, string payerId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(certificateId, out var row) || row.PayerId != payerId)
                return Task.FromResult(WithholdingCancelOutcome.NotFound);
            if (row.State == WithholdingCertificateState.Cancelled)
                return Task.FromResult(WithholdingCancelOutcome.AlreadyCancelled);

            _rows[certificateId] = row with { State = WithholdingCertificateState.Cancelled, ReplacedByCertificateId = replacedByCertificateId };
            ((FakeTransaction)tx).OnRollback(() => { lock (_gate) { _rows[certificateId] = row; } });
            return Task.FromResult(WithholdingCancelOutcome.Cancelled);
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

    internal sealed class Harness : IWithholdingCertificateStoreHarness
    {
        public InMemoryWithholdingCertificateStore InMemory { get; } = new();

        public IWithholdingCertificateStore Store => InMemory;

        public Task<DbTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
            Task.FromResult<DbTransaction>(new FakeTransaction());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
