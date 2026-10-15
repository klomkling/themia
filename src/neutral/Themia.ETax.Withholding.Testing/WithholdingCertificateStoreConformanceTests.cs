using Xunit;

namespace Themia.ETax.Withholding.Testing;

/// <summary>
/// The behaviour every <see cref="IWithholdingCertificateStore"/> must have, so that all adopters share the same key, cancel and
/// payer-scope semantics. Inherit it in your own xUnit project and override <see cref="CreateHarnessAsync"/>; nothing else is needed.
/// Each test gets its own empty store.
/// </summary>
public abstract class WithholdingCertificateStoreConformanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 5, 0, 0, TimeSpan.Zero);
    private const string Payer = "payer-1";

    /// <summary>Creates a store on a fresh empty database.</summary>
    protected abstract Task<IWithholdingCertificateStoreHarness> CreateHarnessAsync();

    // ---- insert, read, key ------------------------------------------------------------------------------------

    /// <summary>A committed insert is readable by id with every column, in state Issued and with no replacement.</summary>
    [Fact]
    public async Task Insert_ShouldStoreEveryColumn_WhenCommitted()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);

        var row = await h.Store.GetAsync(null, doc.Id, Payer);

        Assert.NotNull(row);
        Assert.Equal(doc.Id, row.Id);
        Assert.Equal(doc.Key.SourceEventId, row.SourceEventId);
        Assert.Equal(doc.Key.Revision, row.Revision);
        Assert.Equal(doc.PayerId, row.PayerId);
        Assert.Equal(doc.DocumentNumber, row.DocumentNumber);
        Assert.Equal(doc.IssueDate, row.IssueDate);
        Assert.Equal(doc.SnapshotJson, row.SnapshotJson);
        Assert.Equal(doc.SnapshotSchemaVersion, row.SnapshotSchemaVersion);
        Assert.Equal(doc.SnapshotSha256, row.SnapshotSha256);
        Assert.Equal(doc.TemplateVersion, row.TemplateVersion);
        Assert.Equal(doc.CreatedAt, row.CreatedAt);
        Assert.Equal(WithholdingCertificateState.Issued, row.State);
        Assert.Null(row.ReplacedByCertificateId);
    }

    /// <summary>The snapshot text comes back byte for byte, including non-ASCII.</summary>
    [Fact]
    public async Task Insert_ShouldPreserveTheSnapshotTextExactly()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc() with { SnapshotJson = "{\"b\":1,\"a\":\"สวัสดี Café\",\"c\":[1, 2]}\n" };
        await InsertAsync(h, doc);

        Assert.Equal(doc.SnapshotJson, (await h.Store.GetAsync(null, doc.Id, Payer))!.SnapshotJson);
    }

    /// <summary>A row inserted in a transaction that rolls back never existed.</summary>
    [Fact]
    public async Task Insert_ShouldLeaveNothing_WhenTheTransactionRollsBack()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await using (var tx = await h.BeginTransactionAsync())
        {
            await h.Store.InsertAsync(tx, doc);
            await tx.RollbackAsync();
        }

        Assert.Null(await h.Store.GetAsync(null, doc.Id, Payer));
    }

    /// <summary>The issuing transaction sees its own uncommitted insert, by id and by key.</summary>
    [Fact]
    public async Task Insert_ShouldBeVisibleInsideItsOwnTransaction()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await using var tx = await h.BeginTransactionAsync();
        await h.Store.InsertAsync(tx, doc);

        Assert.NotNull(await h.Store.GetAsync(tx, doc.Id, Payer));
        Assert.Equal(doc.Id, (await h.Store.FindByKeyForIssueAsync(tx, doc.Key))!.Id);
        await tx.RollbackAsync();
    }

    /// <summary>One certificate per (source event, revision).</summary>
    [Fact]
    public async Task Insert_ShouldRejectASecondCertificateWithTheSameKey()
    {
        await using var h = await CreateHarnessAsync();
        await InsertAsync(h, NewDoc());

        await using var tx = await h.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => h.Store.InsertAsync(tx, NewDoc(number: "WHT-2")));
        await tx.RollbackAsync();
    }

    /// <summary>The key is the whole (source event, revision) pair.</summary>
    [Fact]
    public async Task FindByKey_ShouldMatchOnlyTheExactKey()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);

        await using var tx = await h.BeginTransactionAsync();
        Assert.Equal(doc.Id, (await h.Store.FindByKeyForIssueAsync(tx, doc.Key))!.Id);
        Assert.Null(await h.Store.FindByKeyForIssueAsync(tx, new WithholdingCertificateKey(doc.Key.SourceEventId, 2)));
        Assert.Null(await h.Store.FindByKeyForIssueAsync(tx, new WithholdingCertificateKey("other")));
        await tx.RollbackAsync();
    }

    /// <summary>
    /// Two issuers racing on one key must be serialised: the second lookup waits until the first transaction ends, then sees its row.
    /// Otherwise both see "none" and both consume a number.
    /// </summary>
    [Fact]
    public async Task FindByKeyForIssue_ShouldSerialiseRacingIssuersOfTheSameKey()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await using var first = await h.BeginTransactionAsync();
        Assert.Null(await h.Store.FindByKeyForIssueAsync(first, doc.Key));

        await using var second = await h.BeginTransactionAsync();
        var secondLookup = h.Store.FindByKeyForIssueAsync(second, doc.Key);
        await Task.Delay(500);
        Assert.False(secondLookup.IsCompleted, "A racing issuer of the same key must wait for the first transaction to end.");

        await h.Store.InsertAsync(first, doc);
        await first.CommitAsync();

        var seen = await secondLookup.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(doc.Id, seen!.Id);
        await second.RollbackAsync();
    }

    // ---- payer scope: fail closed -------------------------------------------------------------------------------

    /// <summary>Another payer's certificate answers exactly like a missing one when read by id.</summary>
    [Fact]
    public async Task Get_ShouldAnswerNotFound_ForAnotherPayersCertificate()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);

        Assert.NotNull(await h.Store.GetAsync(null, doc.Id, Payer));
        Assert.Null(await h.Store.GetAsync(null, doc.Id, "someone-else"));
        await using var tx = await h.BeginTransactionAsync();
        Assert.Null(await h.Store.GetAsync(tx, doc.Id, "someone-else"));
        await tx.RollbackAsync();
    }

    // ---- cancel for replacement -----------------------------------------------------------------------------------

    /// <summary>Cancelling records the replacement and makes the certificate Cancelled.</summary>
    [Fact]
    public async Task CancelForReplacement_ShouldCancelAndRecordTheReplacement()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);
        var replacement = Guid.NewGuid();

        Assert.Equal(WithholdingCancelOutcome.Cancelled, await CancelAsync(h, doc.Id, replacement));

        var row = (await h.Store.GetAsync(null, doc.Id, Payer))!;
        Assert.Equal(WithholdingCertificateState.Cancelled, row.State);
        Assert.Equal(replacement, row.ReplacedByCertificateId);
    }

    /// <summary>Cancelled is final and the replacement is write-once; an unknown id or another payer's certificate is NotFound.</summary>
    [Fact]
    public async Task CancelForReplacement_ShouldReportAlreadyCancelledNotFoundAndWrongPayer()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);
        var first = Guid.NewGuid();
        await CancelAsync(h, doc.Id, first);

        Assert.Equal(WithholdingCancelOutcome.AlreadyCancelled, await CancelAsync(h, doc.Id));
        Assert.Equal(first, (await h.Store.GetAsync(null, doc.Id, Payer))!.ReplacedByCertificateId);
        Assert.Equal(WithholdingCancelOutcome.NotFound, await CancelAsync(h, Guid.NewGuid()));
        var other = NewDoc(source: "other", number: "WHT-B");
        await InsertAsync(h, other);
        Assert.Equal(WithholdingCancelOutcome.NotFound, await CancelAsync(h, other.Id, null, "someone-else"));
        Assert.Equal(WithholdingCertificateState.Issued, (await h.Store.GetAsync(null, other.Id, Payer))!.State);
    }

    /// <summary>The cancel is part of the issue transaction: roll back and the old certificate is untouched.</summary>
    [Fact]
    public async Task CancelForReplacement_ShouldBeUndone_WhenTheTransactionRollsBack()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);
        await using (var tx = await h.BeginTransactionAsync())
        {
            await h.Store.CancelForReplacementAsync(tx, doc.Id, Guid.NewGuid(), Payer);
            await tx.RollbackAsync();
        }

        var row = (await h.Store.GetAsync(null, doc.Id, Payer))!;
        Assert.Equal(WithholdingCertificateState.Issued, row.State);
        Assert.Null(row.ReplacedByCertificateId);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static NewWithholdingCertificate NewDoc(string source = "pay_1", string number = "WHT-1", string payer = Payer) => new()
    {
        Id = Guid.NewGuid(),
        Key = new WithholdingCertificateKey(source),
        PayerId = payer,
        DocumentNumber = number,
        IssueDate = new DateOnly(2026, 10, 14),
        SnapshotJson = "{\"document_number\":\"" + number + "\"}",
        SnapshotSchemaVersion = 1,
        SnapshotSha256 = new string('a', 64),
        TemplateVersion = "1",
        CreatedAt = Now,
    };

    private static async Task InsertAsync(IWithholdingCertificateStoreHarness h, NewWithholdingCertificate doc)
    {
        await using var tx = await h.BeginTransactionAsync();
        await h.Store.InsertAsync(tx, doc);
        await tx.CommitAsync();
    }

    private static async Task<WithholdingCancelOutcome> CancelAsync(
        IWithholdingCertificateStoreHarness h, Guid id, Guid? replacedBy = null, string payer = Payer)
    {
        await using var tx = await h.BeginTransactionAsync();
        var outcome = await h.Store.CancelForReplacementAsync(tx, id, replacedBy ?? Guid.NewGuid(), payer);
        await tx.CommitAsync();
        return outcome;
    }
}
