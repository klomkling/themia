using Xunit;

namespace Themia.ETax.Testing;

/// <summary>
/// The behaviour every <see cref="ITaxDocumentStore"/> must have, so that all adopters share the same transition and lease
/// semantics. Inherit it in your own xUnit project and override <see cref="CreateHarnessAsync"/>; nothing else is needed.
/// Each test gets its own empty store.
/// </summary>
public abstract class TaxDocumentStoreConformanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 5, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private const string Seller = "seller-1";

    /// <summary>Creates a store on a fresh empty database.</summary>
    protected abstract Task<ITaxDocumentStoreHarness> CreateHarnessAsync();

    // ---- insert, read, key ------------------------------------------------------------------------------------

    /// <summary>A committed insert is readable by id with every column, in state Issued with no lease.</summary>
    [Fact]
    public async Task Insert_ShouldStoreEveryColumn_WhenCommitted()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);

        var row = await h.Store.GetAsync(null, doc.Id, Seller);

        Assert.NotNull(row);
        Assert.Equal(doc.Key.Kind, row.Kind);
        Assert.Equal(doc.Key.SourceEventId, row.SourceEventId);
        Assert.Equal(doc.Key.Revision, row.Revision);
        Assert.Equal(doc.SellerId, row.SellerId);
        Assert.Equal(doc.DocumentNumber, row.DocumentNumber);
        Assert.Equal(doc.IssueDate, row.IssueDate);
        Assert.Equal(doc.SnapshotJson, row.SnapshotJson);
        Assert.Equal(doc.SnapshotSchemaVersion, row.SnapshotSchemaVersion);
        Assert.Equal(doc.SnapshotSha256, row.SnapshotSha256);
        Assert.Equal(doc.TemplateVersion, row.TemplateVersion);
        Assert.Equal(doc.BuyerEmail, row.BuyerEmail);
        Assert.Equal(TaxDocumentState.Issued, row.State);
        Assert.Null(row.ClaimToken);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.OriginalRenderedAt);
        Assert.Null(row.ReplacedByDocumentId);
    }

    /// <summary>The snapshot text comes back byte for byte, including non-ASCII.</summary>
    [Fact]
    public async Task Insert_ShouldPreserveTheSnapshotTextExactly()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc() with { SnapshotJson = "{\"b\":1,\"a\":\"สวัสดี Café\",\"c\":[1, 2]}\n" };
        await InsertAsync(h, doc);

        Assert.Equal(doc.SnapshotJson, (await h.Store.GetAsync(null, doc.Id, Seller))!.SnapshotJson);
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

        Assert.Null(await h.Store.GetAsync(null, doc.Id, Seller));
    }

    /// <summary>The issuing transaction sees its own uncommitted insert, by id and by key.</summary>
    [Fact]
    public async Task Insert_ShouldBeVisibleInsideItsOwnTransaction()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await using var tx = await h.BeginTransactionAsync();
        await h.Store.InsertAsync(tx, doc);

        Assert.NotNull(await h.Store.GetAsync(tx, doc.Id, Seller));
        Assert.Equal(doc.Id, (await h.Store.FindByKeyForIssueAsync(tx, doc.Key))!.Id);
        await tx.RollbackAsync();
    }

    /// <summary>One document per (kind, source event, revision).</summary>
    [Fact]
    public async Task Insert_ShouldRejectASecondDocumentWithTheSameKey()
    {
        await using var h = await CreateHarnessAsync();
        await InsertAsync(h, NewDoc());

        await using var tx = await h.BeginTransactionAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => h.Store.InsertAsync(tx, NewDoc(number: "TAX-2")));
        await tx.RollbackAsync();
    }

    /// <summary>The key is the whole (kind, source, revision) triple.</summary>
    [Fact]
    public async Task FindByKey_ShouldMatchOnlyTheExactKey()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);

        await using var tx = await h.BeginTransactionAsync();
        Assert.Equal(doc.Id, (await h.Store.FindByKeyForIssueAsync(tx, doc.Key))!.Id);
        Assert.Null(await h.Store.FindByKeyForIssueAsync(tx, new TaxDocumentKey(doc.Key.Kind, doc.Key.SourceEventId, 2)));
        Assert.Null(await h.Store.FindByKeyForIssueAsync(tx, new TaxDocumentKey(TaxDocumentKind.CreditNote, doc.Key.SourceEventId)));
        Assert.Null(await h.Store.FindByKeyForIssueAsync(tx, new TaxDocumentKey(doc.Key.Kind, "other")));
        await tx.RollbackAsync();
    }

    /// <summary>
    /// Two issuers racing on one key must be serialised: the second lookup waits until the first transaction ends, then sees its
    /// row. Otherwise both see "none" and both consume a number.
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

    // ---- claim / lease ----------------------------------------------------------------------------------------

    /// <summary>A claim stamps the token and an expiry of now + lease on the claimed rows.</summary>
    [Fact]
    public async Task ClaimDue_ShouldStampTheLease()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await InsertAndGetAsync(h);
        var token = Guid.NewGuid();

        var claimed = await h.Store.ClaimDueAsync(10, Now, Lease, token);

        var row = Assert.Single(claimed);
        Assert.Equal(doc.Id, row.Id);
        Assert.Equal(token, row.ClaimToken);
        Assert.Equal(Now + Lease, row.ClaimExpiresAt);
        Assert.Equal(token, (await h.Store.GetAsync(null, doc.Id, Seller))!.ClaimToken);
    }

    /// <summary>A live lease keeps the row from being claimed again.</summary>
    [Fact]
    public async Task ClaimDue_ShouldNotClaimAgain_WhileTheLeaseIsLive()
    {
        await using var h = await CreateHarnessAsync();
        await InsertAsync(h, NewDoc());
        await h.Store.ClaimDueAsync(10, Now, Lease, Guid.NewGuid());

        Assert.Empty(await h.Store.ClaimDueAsync(10, Now + Lease - TimeSpan.FromSeconds(1), Lease, Guid.NewGuid()));
    }

    /// <summary>A lapsed lease is claimable again; this is the sweeper.</summary>
    [Fact]
    public async Task ClaimDue_ShouldClaimAgain_AfterTheLeaseExpires()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);
        await h.Store.ClaimDueAsync(10, Now, Lease, Guid.NewGuid());
        var second = Guid.NewGuid();

        var again = await h.Store.ClaimDueAsync(10, Now + Lease, Lease, second);

        Assert.Equal(second, Assert.Single(again).ClaimToken);
    }

    /// <summary>Backoff: a row is not due before its next attempt time.</summary>
    [Fact]
    public async Task ClaimDue_ShouldSkipARowUntilItsNextAttemptTime()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);
        var token = Guid.NewGuid();
        await h.Store.ClaimDueAsync(10, Now, Lease, token);
        Assert.True(await h.Store.ReleaseAsync(doc.Id, token, 1, Now + TimeSpan.FromMinutes(10), "send_transient"));

        Assert.Empty(await h.Store.ClaimDueAsync(10, Now + TimeSpan.FromMinutes(9), Lease, Guid.NewGuid()));
        Assert.Single(await h.Store.ClaimDueAsync(10, Now + TimeSpan.FromMinutes(10), Lease, Guid.NewGuid()));
    }

    /// <summary>The batch size is honoured.</summary>
    [Fact]
    public async Task ClaimDue_ShouldNotClaimMoreThanMax()
    {
        await using var h = await CreateHarnessAsync();
        for (var i = 0; i < 3; i++)
            await InsertAsync(h, NewDoc(source: $"pay_{i}", number: $"TAX-{i}"));

        Assert.Equal(2, (await h.Store.ClaimDueAsync(2, Now, Lease, Guid.NewGuid())).Count);
    }

    /// <summary>Only Issued and OriginalPrepared rows are ever claimed.</summary>
    [Fact]
    public async Task ClaimDue_ShouldIgnoreSentFailedAndCancelledDocuments()
    {
        await using var h = await CreateHarnessAsync();
        await InsertDrivenAsync(h, NewDoc(source: "sent", number: "A"), TaxDocumentState.Sent);
        await InsertDrivenAsync(h, NewDoc(source: "failed", number: "B"), TaxDocumentState.Failed);
        var cancelled = await InsertAndGetAsync(h, NewDoc(source: "cancelled", number: "C"));
        await CancelAsync(h, cancelled.Id);

        Assert.Empty(await h.Store.ClaimDueAsync(10, Now + TimeSpan.FromHours(1), Lease, Guid.NewGuid()));
    }

    /// <summary>Two workers claiming at once never get the same row.</summary>
    [Fact]
    public async Task ClaimDue_ShouldGiveARowToExactlyOneOfTwoConcurrentWorkers()
    {
        await using var h = await CreateHarnessAsync();
        await InsertAsync(h, NewDoc());

        var results = await Task.WhenAll(
            h.Store.ClaimDueAsync(10, Now, Lease, Guid.NewGuid()),
            h.Store.ClaimDueAsync(10, Now, Lease, Guid.NewGuid()));

        Assert.Equal(1, results.Sum(r => r.Count));
    }

    // ---- ORIGINAL evidence ------------------------------------------------------------------------------------

    /// <summary>Recording an ORIGINAL moves to OriginalPrepared and keeps the first hash and time.</summary>
    [Fact]
    public async Task RecordOriginal_ShouldMoveToOriginalPrepared_AndSetTheFirstValues()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);
        var token = Guid.NewGuid();
        await h.Store.ClaimDueAsync(10, Now, Lease, token);

        Assert.True(await h.Store.RecordOriginalAsync(doc.Id, token, Now, "hash-1", "chrome-1|n1"));

        var row = (await h.Store.GetAsync(null, doc.Id, Seller))!;
        Assert.Equal(TaxDocumentState.OriginalPrepared, row.State);
        Assert.Equal("hash-1", row.PdfSha256);
        Assert.Equal(Now, row.OriginalRenderedAt);
        var evidence = Assert.Single(await h.Store.GetEvidenceAsync(doc.Id));
        Assert.Equal(new TaxDocumentRenderEvidence("hash-1", "chrome-1|n1"), evidence);
    }

    /// <summary>A later render appends evidence but never overwrites the first hash or time.</summary>
    [Fact]
    public async Task RecordOriginal_ShouldAppendEvidenceWithoutOverwritingTheFirstValues()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);
        var first = Guid.NewGuid();
        await h.Store.ClaimDueAsync(10, Now, Lease, first);
        await h.Store.RecordOriginalAsync(doc.Id, first, Now, "hash-1", "chrome-1|n1");
        await h.Store.ReleaseAsync(doc.Id, first, 1, Now, "send_transient");
        var second = Guid.NewGuid();
        var later = Now + TimeSpan.FromHours(1);
        await h.Store.ClaimDueAsync(10, later, Lease, second);

        Assert.True(await h.Store.RecordOriginalAsync(doc.Id, second, later, "hash-2", "chrome-2|n1"));

        var row = (await h.Store.GetAsync(null, doc.Id, Seller))!;
        Assert.Equal("hash-1", row.PdfSha256);
        Assert.Equal(Now, row.OriginalRenderedAt);
        Assert.Equal(
            [new TaxDocumentRenderEvidence("hash-1", "chrome-1|n1"), new TaxDocumentRenderEvidence("hash-2", "chrome-2|n1")],
            await h.Store.GetEvidenceAsync(doc.Id));
    }

    /// <summary>No evidence, no rows.</summary>
    [Fact]
    public async Task GetEvidence_ShouldBeEmpty_WhenNothingWasRendered()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);

        Assert.Empty(await h.Store.GetEvidenceAsync(doc.Id));
    }

    /// <summary>Only the lease holder, while the lease is live, may record an ORIGINAL.</summary>
    [Fact]
    public async Task RecordOriginal_ShouldBeRefused_WhenTheTokenIsWrongOrTheLeaseHasLapsed()
    {
        await using var h = await CreateHarnessAsync();
        var doc = NewDoc();
        await InsertAsync(h, doc);
        var token = Guid.NewGuid();
        await h.Store.ClaimDueAsync(10, Now, Lease, token);

        Assert.False(await h.Store.RecordOriginalAsync(doc.Id, Guid.NewGuid(), Now, "h", "v"));
        Assert.False(await h.Store.RecordOriginalAsync(doc.Id, token, Now + Lease, "h", "v"));
        Assert.Equal(TaxDocumentState.Issued, (await h.Store.GetAsync(null, doc.Id, Seller))!.State);
        Assert.Empty(await h.Store.GetEvidenceAsync(doc.Id));
    }

    // ---- send ------------------------------------------------------------------------------------------------

    /// <summary>The pre-send re-check extends the lease so a cancel cannot slip in during the send.</summary>
    [Fact]
    public async Task BeginSend_ShouldExtendTheLease_WhenOriginalPreparedAndHeld()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await PrepareOriginalAsync(h);
        var later = Now + TimeSpan.FromSeconds(90);

        Assert.True(await h.Store.BeginSendAsync(doc.Id, doc.Token, later, Lease));

        Assert.Equal(later + Lease, (await h.Store.GetAsync(null, doc.Id, Seller))!.ClaimExpiresAt);
    }

    /// <summary>Not sendable: wrong token, lapsed lease, or not yet OriginalPrepared.</summary>
    [Fact]
    public async Task BeginSend_ShouldBeRefused_WhenNotHeldOrNotOriginalPrepared()
    {
        await using var h = await CreateHarnessAsync();
        var prepared = await PrepareOriginalAsync(h);
        var issued = await InsertAndGetAsync(h, NewDoc(source: "issued", number: "B"));
        var issuedToken = Guid.NewGuid();
        await h.Store.ClaimDueAsync(10, Now, Lease, issuedToken);

        Assert.False(await h.Store.BeginSendAsync(prepared.Id, Guid.NewGuid(), Now, Lease));
        Assert.False(await h.Store.BeginSendAsync(prepared.Id, prepared.Token, Now + Lease, Lease));
        Assert.False(await h.Store.BeginSendAsync(issued.Id, issuedToken, Now, Lease));
    }

    /// <summary>A cancelled document cannot begin to send, even for a worker that rendered it earlier.</summary>
    [Fact]
    public async Task BeginSend_ShouldBeRefused_WhenTheDocumentWasCancelledAfterTheLeaseLapsed()
    {
        await using var h = await CreateHarnessAsync();
        var prepared = await PrepareOriginalAsync(h);
        var afterExpiry = Now + Lease + TimeSpan.FromSeconds(1);
        Assert.Equal(TaxDocumentCancelOutcome.Cancelled, await CancelAsync(h, prepared.Id, afterExpiry));

        Assert.False(await h.Store.BeginSendAsync(prepared.Id, prepared.Token, afterExpiry, Lease));
        Assert.False(await h.Store.MarkSentAsync(prepared.Id, prepared.Token, afterExpiry, "m"));
    }

    /// <summary>Marking sent records the time and message id and releases the lease.</summary>
    [Fact]
    public async Task MarkSent_ShouldRecordTheSendAndClearTheLease()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await PrepareOriginalAsync(h);
        Assert.True(await h.Store.BeginSendAsync(doc.Id, doc.Token, Now, Lease));

        Assert.True(await h.Store.MarkSentAsync(doc.Id, doc.Token, Now, doc.Id.ToString()));

        var row = (await h.Store.GetAsync(null, doc.Id, Seller))!;
        Assert.Equal(TaxDocumentState.Sent, row.State);
        Assert.Equal(Now, row.SentAt);
        Assert.Equal(doc.Id.ToString(), row.MessageId);
        Assert.Null(row.ClaimToken);
    }

    /// <summary>Only the holder, and only from OriginalPrepared.</summary>
    [Fact]
    public async Task MarkSent_ShouldBeRefused_WhenTheTokenIsWrongOrTheStateIsNotOriginalPrepared()
    {
        await using var h = await CreateHarnessAsync();
        var prepared = await PrepareOriginalAsync(h);
        var issued = await InsertAndGetAsync(h, NewDoc(source: "issued", number: "B"));
        var issuedToken = Guid.NewGuid();
        await h.Store.ClaimDueAsync(10, Now, Lease, issuedToken);

        Assert.False(await h.Store.MarkSentAsync(prepared.Id, Guid.NewGuid(), Now, "m"));
        Assert.False(await h.Store.MarkSentAsync(issued.Id, issuedToken, Now, "m"));
    }

    // ---- retry, fail, requeue -----------------------------------------------------------------------------------

    /// <summary>A retried failure keeps the state and records attempts, code and backoff, and frees the lease.</summary>
    [Fact]
    public async Task Release_ShouldKeepTheStateAndRecordTheFailure()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await PrepareOriginalAsync(h);
        var next = Now + TimeSpan.FromMinutes(5);

        Assert.True(await h.Store.ReleaseAsync(doc.Id, doc.Token, 2, next, "send_transient"));

        var row = (await h.Store.GetAsync(null, doc.Id, Seller))!;
        Assert.Equal(TaxDocumentState.OriginalPrepared, row.State);
        Assert.Equal(2, row.Attempts);
        Assert.Equal(next, row.NextAttemptAt);
        Assert.Equal("send_transient", row.LastErrorCode);
        Assert.Null(row.ClaimToken);
        Assert.False(await h.Store.ReleaseAsync(doc.Id, doc.Token, 3, next, "x"), "The lease is gone, so a second release must be refused.");
    }

    /// <summary>
    /// After a send timeout the lease must outlive the failed attempt: a mailer that ignored its token may still be delivering, and the lease is
    /// what keeps a cancel out until it lapses.
    /// </summary>
    [Fact]
    public async Task Release_ShouldLeaveTheLeaseToLapse_WhenAskedToKeepIt()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await PrepareOriginalAsync(h);
        var halfway = Now + TimeSpan.FromSeconds(30);

        Assert.True(await h.Store.ReleaseAsync(doc.Id, doc.Token, 1, Now + Lease, "send_timeout", TaxDocumentLeaseHandling.KeepUntilExpiry));

        var row = (await h.Store.GetAsync(null, doc.Id, Seller))!;
        Assert.Equal(TaxDocumentState.OriginalPrepared, row.State);
        Assert.Equal(1, row.Attempts);
        Assert.Equal("send_timeout", row.LastErrorCode);
        Assert.Equal(doc.Token, row.ClaimToken);
        Assert.Equal(Now + Lease, row.ClaimExpiresAt);
        Assert.Equal(TaxDocumentCancelOutcome.LeaseHeld, await CancelAsync(h, doc.Id, halfway));
        Assert.Empty(await h.Store.ClaimDueAsync(10, halfway, Lease, Guid.NewGuid()));
        Assert.Equal(TaxDocumentCancelOutcome.Cancelled, await CancelAsync(h, doc.Id, Now + Lease));
    }

    /// <summary>A kept lease is still released by the normal path: the default clears it.</summary>
    [Fact]
    public async Task Fail_ShouldLeaveTheLeaseToLapse_WhenAskedToKeepIt()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await PrepareOriginalAsync(h);

        Assert.True(await h.Store.FailAsync(doc.Id, doc.Token, 5, "send_timeout", TaxDocumentLeaseHandling.KeepUntilExpiry));

        var row = (await h.Store.GetAsync(null, doc.Id, Seller))!;
        Assert.Equal(TaxDocumentState.Failed, row.State);
        Assert.Equal(doc.Token, row.ClaimToken);
        Assert.Equal(TaxDocumentCancelOutcome.LeaseHeld, await CancelAsync(h, doc.Id, Now + TimeSpan.FromSeconds(30)));
        Assert.Equal(TaxDocumentCancelOutcome.Cancelled, await CancelAsync(h, doc.Id, Now + Lease));
    }

    /// <summary>Failing is terminal for automation.</summary>
    [Fact]
    public async Task Fail_ShouldMoveToFailed_AndBeTerminalForClaiming()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await PrepareOriginalAsync(h);

        Assert.True(await h.Store.FailAsync(doc.Id, doc.Token, 5, "send_permanent"));

        var row = (await h.Store.GetAsync(null, doc.Id, Seller))!;
        Assert.Equal(TaxDocumentState.Failed, row.State);
        Assert.Equal(5, row.Attempts);
        Assert.Equal("send_permanent", row.LastErrorCode);
        Assert.Null(row.ClaimToken);
        Assert.Empty(await h.Store.ClaimDueAsync(10, Now + TimeSpan.FromDays(1), Lease, Guid.NewGuid()));
        Assert.False(await h.Store.FailAsync(doc.Id, doc.Token, 6, "again"));
    }

    /// <summary>A failed document with no ORIGINAL goes back to Issued; one with an ORIGINAL keeps it (not burned).</summary>
    [Fact]
    public async Task Requeue_ShouldReturnToIssuedOrOriginalPrepared_AndResetTheAttempts()
    {
        await using var h = await CreateHarnessAsync();
        var withOriginal = await PrepareOriginalAsync(h);
        await h.Store.FailAsync(withOriginal.Id, withOriginal.Token, 5, "send_transient");
        var withoutOriginal = await InsertDrivenAsync(h, NewDoc(source: "noorig", number: "B"), TaxDocumentState.Failed);

        Assert.True(await h.Store.RequeueAsync(withOriginal.Id, Seller, "operator-1", Now));
        Assert.True(await h.Store.RequeueAsync(withoutOriginal.Id, Seller, "operator-1", Now));

        var a = (await h.Store.GetAsync(null, withOriginal.Id, Seller))!;
        var b = (await h.Store.GetAsync(null, withoutOriginal.Id, Seller))!;
        Assert.Equal(TaxDocumentState.OriginalPrepared, a.State);
        Assert.Equal(TaxDocumentState.Issued, b.State);
        Assert.All(new[] { a, b }, r =>
        {
            Assert.Equal(0, r.Attempts);
            Assert.Null(r.NextAttemptAt);
            Assert.Null(r.LastErrorCode);
        });
        Assert.Equal(2, (await h.Store.ClaimDueAsync(10, Now, Lease, Guid.NewGuid())).Count);
    }

    /// <summary>Requeue is for Failed documents only.</summary>
    [Fact]
    public async Task Requeue_ShouldBeRefused_WhenTheDocumentIsNotFailed()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await InsertAndGetAsync(h);

        Assert.False(await h.Store.RequeueAsync(doc.Id, Seller, "operator-1", Now));
        Assert.False(await h.Store.RequeueAsync(Guid.NewGuid(), Seller, "operator-1", Now));
    }

    /// <summary>The recipient can be corrected until the document is sent or cancelled.</summary>
    [Fact]
    public async Task UpdateBuyerEmail_ShouldWorkUntilSentOrCancelled()
    {
        await using var h = await CreateHarnessAsync();
        var failed = await InsertDrivenAsync(h, NewDoc(source: "b", number: "B"), TaxDocumentState.Failed);
        var sent = await InsertDrivenAsync(h, NewDoc(source: "c", number: "C"), TaxDocumentState.Sent);
        var cancelled = await InsertAndGetAsync(h, NewDoc(source: "d", number: "D"));
        await CancelAsync(h, cancelled.Id);
        var open = await InsertAndGetAsync(h, NewDoc(source: "a", number: "A"));

        Assert.True(await h.Store.UpdateBuyerEmailAsync(open.Id, Seller, "new@example.test"));
        Assert.True(await h.Store.UpdateBuyerEmailAsync(failed.Id, Seller, "new@example.test"));
        Assert.False(await h.Store.UpdateBuyerEmailAsync(sent.Id, Seller, "new@example.test"));
        Assert.False(await h.Store.UpdateBuyerEmailAsync(cancelled.Id, Seller, "new@example.test"));
        Assert.Equal("new@example.test", (await h.Store.GetAsync(null, open.Id, Seller))!.BuyerEmail);
    }

    // ---- seller scope: fail closed -------------------------------------------------------------------------------

    /// <summary>Another seller's document answers exactly like a missing one when read by id.</summary>
    [Fact]
    public async Task Get_ShouldAnswerNotFound_ForAnotherSellersDocument()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await InsertAndGetAsync(h);

        Assert.NotNull(await h.Store.GetAsync(null, doc.Id, Seller));
        Assert.Null(await h.Store.GetAsync(null, doc.Id, "someone-else"));
        await using var tx = await h.BeginTransactionAsync();
        Assert.Null(await h.Store.GetAsync(tx, doc.Id, "someone-else"));
        await tx.RollbackAsync();
    }

    /// <summary>Another seller cannot requeue a document, and nothing about it changes.</summary>
    [Fact]
    public async Task Requeue_ShouldAnswerNotFound_ForAnotherSellersDocument()
    {
        await using var h = await CreateHarnessAsync();
        var failed = await InsertDrivenAsync(h, NewDoc(), TaxDocumentState.Failed);

        Assert.False(await h.Store.RequeueAsync(failed.Id, "someone-else", "operator-1", Now));

        Assert.Equal(TaxDocumentState.Failed, (await h.Store.GetAsync(null, failed.Id, Seller))!.State);
        Assert.True(await h.Store.RequeueAsync(failed.Id, Seller, "operator-1", Now));
    }

    /// <summary>Another seller cannot change the recipient.</summary>
    [Fact]
    public async Task UpdateBuyerEmail_ShouldAnswerNotFound_ForAnotherSellersDocument()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await InsertAndGetAsync(h);

        Assert.False(await h.Store.UpdateBuyerEmailAsync(doc.Id, "someone-else", "evil@example.test"));

        Assert.Equal(doc.BuyerEmail, (await h.Store.GetAsync(null, doc.Id, Seller))!.BuyerEmail);
    }

    // ---- cancel for replacement (F2) ---------------------------------------------------------------------------

    /// <summary>Cancelling records the replacement and makes the document unclaimable.</summary>
    [Fact]
    public async Task CancelForReplacement_ShouldCancelAndRecordTheReplacement()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await InsertAndGetAsync(h);
        var replacement = Guid.NewGuid();

        Assert.Equal(TaxDocumentCancelOutcome.Cancelled, await CancelAsync(h, doc.Id, Now, replacement));

        var row = (await h.Store.GetAsync(null, doc.Id, Seller))!;
        Assert.Equal(TaxDocumentState.Cancelled, row.State);
        Assert.Equal(replacement, row.ReplacedByDocumentId);
        Assert.Empty(await h.Store.ClaimDueAsync(10, Now + TimeSpan.FromDays(1), Lease, Guid.NewGuid()));
    }

    /// <summary>Every state except Cancelled can be replaced; a Sent one too (the D2 exception).</summary>
    [Theory]
    [InlineData(TaxDocumentState.Issued)]
    [InlineData(TaxDocumentState.OriginalPrepared)]
    [InlineData(TaxDocumentState.Sent)]
    [InlineData(TaxDocumentState.Failed)]
    public async Task CancelForReplacement_ShouldCancelFromAnyOtherState(TaxDocumentState state)
    {
        await using var h = await CreateHarnessAsync();
        var doc = await InsertDrivenAsync(h, NewDoc(), state);

        Assert.Equal(TaxDocumentCancelOutcome.Cancelled, await CancelAsync(h, doc.Id, Now + TimeSpan.FromHours(1)));
    }

    /// <summary>F2: a document being worked on cannot be cancelled, so it can never be sent as ORIGINAL afterwards.</summary>
    [Fact]
    public async Task CancelForReplacement_ShouldBeRefused_WhileALiveLeaseIsHeld()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await PrepareOriginalAsync(h);

        Assert.Equal(TaxDocumentCancelOutcome.LeaseHeld, await CancelAsync(h, doc.Id, Now + TimeSpan.FromSeconds(30)));

        Assert.Equal(TaxDocumentState.OriginalPrepared, (await h.Store.GetAsync(null, doc.Id, Seller))!.State);
    }

    /// <summary>A lease that has lapsed no longer protects the document.</summary>
    [Fact]
    public async Task CancelForReplacement_ShouldSucceed_AfterTheLeaseLapses()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await PrepareOriginalAsync(h);

        Assert.Equal(TaxDocumentCancelOutcome.Cancelled, await CancelAsync(h, doc.Id, Now + Lease));
    }

    /// <summary>Cancelled is terminal, and only the same seller may replace.</summary>
    [Fact]
    public async Task CancelForReplacement_ShouldReportAlreadyCancelledNotFoundAndWrongSeller()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await InsertAndGetAsync(h);
        await CancelAsync(h, doc.Id);

        Assert.Equal(TaxDocumentCancelOutcome.AlreadyCancelled, await CancelAsync(h, doc.Id));
        Assert.Equal(TaxDocumentCancelOutcome.NotFound, await CancelAsync(h, Guid.NewGuid()));
        var other = await InsertAndGetAsync(h, NewDoc(source: "other", number: "B"));
        Assert.Equal(TaxDocumentCancelOutcome.NotFound, await CancelAsync(h, other.Id, Now, null, "someone-else"));
        Assert.Equal(TaxDocumentState.Issued, (await h.Store.GetAsync(null, other.Id, Seller))!.State);
    }

    /// <summary>The cancel is part of the issue transaction: roll back and the old document is untouched.</summary>
    [Fact]
    public async Task CancelForReplacement_ShouldBeUndone_WhenTheTransactionRollsBack()
    {
        await using var h = await CreateHarnessAsync();
        var doc = await InsertAndGetAsync(h);
        await using (var tx = await h.BeginTransactionAsync())
        {
            await h.Store.CancelForReplacementAsync(tx, doc.Id, Guid.NewGuid(), Seller, Now);
            await tx.RollbackAsync();
        }

        Assert.Equal(TaxDocumentState.Issued, (await h.Store.GetAsync(null, doc.Id, Seller))!.State);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static NewTaxDocument NewDoc(string source = "pay_1", string number = "TAX-1", string seller = Seller) => new()
    {
        Id = Guid.NewGuid(),
        Key = new TaxDocumentKey(TaxDocumentKind.TaxInvoiceReceipt, source),
        SellerId = seller,
        DocumentNumber = number,
        IssueDate = new DateOnly(2026, 10, 14),
        SnapshotJson = "{\"document_number\":\"" + number + "\"}",
        SnapshotSchemaVersion = 1,
        SnapshotSha256 = new string('a', 64),
        TemplateVersion = "1",
        BuyerEmail = "buyer@example.test",
        CreatedAt = Now,
    };

    private static async Task InsertAsync(ITaxDocumentStoreHarness h, NewTaxDocument doc)
    {
        await using var tx = await h.BeginTransactionAsync();
        await h.Store.InsertAsync(tx, doc);
        await tx.CommitAsync();
    }

    private static async Task<TaxDocumentRecord> InsertAndGetAsync(ITaxDocumentStoreHarness h, NewTaxDocument? doc = null)
    {
        doc ??= NewDoc();
        await InsertAsync(h, doc);
        return (await h.Store.GetAsync(null, doc.Id, Seller))!;
    }

    // Inserts a document, claims it and records its ORIGINAL: OriginalPrepared with a live lease (taken at Now).
    private static async Task<(Guid Id, Guid Token)> PrepareOriginalAsync(ITaxDocumentStoreHarness h)
    {
        var doc = NewDoc(source: "prepared", number: "P-1");
        await InsertAsync(h, doc);
        var token = Guid.NewGuid();
        await h.Store.ClaimDueAsync(10, Now, Lease, token);
        Assert.True(await h.Store.RecordOriginalAsync(doc.Id, token, Now, "hash-1", "chrome-1|n1"));
        return (doc.Id, token);
    }

    // Inserts a document and takes it to the wanted state through the store's own transitions, leaving no lease behind.
    // One at a time: the claim below takes every due document, so another due one would be swept into it.
    private static async Task<TaxDocumentRecord> InsertDrivenAsync(ITaxDocumentStoreHarness h, NewTaxDocument doc, TaxDocumentState state)
    {
        var row = await InsertAndGetAsync(h, doc);
        await DriveAsync(h, row.Id, state);
        return (await h.Store.GetAsync(null, row.Id, Seller))!;
    }

    private static async Task DriveAsync(ITaxDocumentStoreHarness h, Guid id, TaxDocumentState state)
    {
        if (state == TaxDocumentState.Issued)
            return;

        var token = Guid.NewGuid();
        var at = Now + TimeSpan.FromDays(1);
        Assert.Single(await h.Store.ClaimDueAsync(100, at, Lease, token), r => r.Id == id);
        switch (state)
        {
            case TaxDocumentState.OriginalPrepared:
                Assert.True(await h.Store.RecordOriginalAsync(id, token, at, "hash-1", "chrome-1|n1"));
                Assert.True(await h.Store.ReleaseAsync(id, token, 1, at, "send_transient"));
                break;
            case TaxDocumentState.Sent:
                Assert.True(await h.Store.RecordOriginalAsync(id, token, at, "hash-1", "chrome-1|n1"));
                Assert.True(await h.Store.BeginSendAsync(id, token, at, Lease));
                Assert.True(await h.Store.MarkSentAsync(id, token, at, id.ToString()));
                break;
            case TaxDocumentState.Failed:
                Assert.True(await h.Store.FailAsync(id, token, 5, "send_permanent"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state));
        }
    }

    private static async Task<TaxDocumentCancelOutcome> CancelAsync(
        ITaxDocumentStoreHarness h, Guid id, DateTimeOffset? at = null, Guid? replacedBy = null, string seller = Seller)
    {
        await using var tx = await h.BeginTransactionAsync();
        var outcome = await h.Store.CancelForReplacementAsync(tx, id, replacedBy ?? Guid.NewGuid(), seller, at ?? Now + TimeSpan.FromDays(2));
        if (outcome == TaxDocumentCancelOutcome.Cancelled)
            await tx.CommitAsync();
        else
            await tx.RollbackAsync();
        return outcome;
    }
}
