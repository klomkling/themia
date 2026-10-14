using Xunit;

namespace Themia.ETax.Tests;

public sealed class TaxDocumentServiceTests
{
    private readonly Flow _flow = new();

    [Fact]
    public async Task IssueAsync_ShouldStoreTheSnapshotWithTheAllocatedNumberAndAVerifiableHash()
    {
        var doc = await _flow.IssuedAsync();

        Assert.Equal("TAX-0001", doc.DocumentNumber);
        Assert.Equal(TaxDocumentState.Issued, doc.State);
        Assert.Equal(TaxDocumentTemplates.CurrentVersion, doc.TemplateVersion);
        var snapshot = TaxDocumentSnapshotSerializer.Deserialize(doc.SnapshotJson, doc.SnapshotSha256);
        Assert.Equal("TAX-0001", snapshot.DocumentNumber);
        Assert.Equal(Samples.Receipt().Buyer, snapshot.Buyer);
    }

    [Fact]
    public async Task IssueAsync_ShouldReturnAlreadyIssuedAndConsumeNoNumber_WhenTheKeyExists()
    {
        var first = await _flow.IssuedAsync();

        var again = await _flow.IssueAsync(Flow.Request());

        Assert.Equal(TaxDocumentIssueOutcome.AlreadyIssued, again.Outcome);
        Assert.Equal(first.Id, again.DocumentId);
        Assert.Equal(1, _flow.Numbers.Allocated);
    }

    [Fact]
    public async Task IssueAsync_ShouldReportASourceConflictAndNeverLeakTheOtherSellersDocument_WhenTheKeyBelongsToAnotherSeller()
    {
        var first = await _flow.IssuedAsync();

        var result = await _flow.IssueAsync(Flow.Request(seller: "seller-2"));

        Assert.Equal(TaxDocumentIssueOutcome.SourceConflict, result.Outcome);
        Assert.Null(result.DocumentId);
        Assert.Empty(result.Errors);
        Assert.Equal(1, _flow.Numbers.Allocated);
        Assert.Single(_flow.InMemory.Rows);
        Assert.Equal(first.Id, Assert.Single(_flow.InMemory.Rows).Id);
    }

    [Fact]
    public async Task IssueAsync_ShouldStillAnswerAlreadyIssued_ForTheSameSeller()
    {
        var first = await _flow.IssuedAsync();

        var again = await _flow.IssueAsync(Flow.Request());

        Assert.Equal(TaxDocumentIssueOutcome.AlreadyIssued, again.Outcome);
        Assert.Equal(first.Id, again.DocumentId);
    }

    [Fact]
    public async Task IssueAsync_ShouldIssueASecondRevisionOfTheSameEvent()
    {
        await _flow.IssuedAsync();
        var revision2 = new IssueTaxDocumentRequest
        {
            Key = new TaxDocumentKey(TaxDocumentKind.TaxInvoiceReceipt, "pay_1", 2),
            SellerId = Flow.Seller,
            Snapshot = Samples.Receipt(),
        };

        var result = await _flow.IssueAsync(revision2);

        Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
        Assert.Equal(2, _flow.Numbers.Allocated);
    }

    [Fact]
    public async Task IssueAsync_ShouldFailValidationAndConsumeNoNumber_WhenTheSnapshotIsInvalid()
    {
        var invalid = Samples.Receipt() with { Buyer = new TaxParty { Name = "中文公司" } };

        var result = await _flow.IssueAsync(Flow.Request(snapshot: invalid));

        Assert.Equal(TaxDocumentIssueOutcome.ValidationFailed, result.Outcome);
        var error = Assert.Single(result.Errors);
        Assert.Equal(new TaxDocumentValidationError(TaxDocumentValidationCode.UnsupportedCharacter, "buyer.name"), error);
        Assert.Equal(0, _flow.Numbers.Allocated);
        Assert.Empty(_flow.InMemory.Rows);
    }

    [Fact]
    public async Task IssueAsync_ShouldFailValidation_WhenTheKeyKindDiffersFromTheSnapshotKind()
    {
        var request = new IssueTaxDocumentRequest
        {
            Key = new TaxDocumentKey(TaxDocumentKind.CreditNote, "refund_1"),
            SellerId = Flow.Seller,
            Snapshot = Samples.Receipt(),
        };

        var result = await _flow.IssueAsync(request);

        Assert.Equal(TaxDocumentIssueOutcome.ValidationFailed, result.Outcome);
        Assert.Contains(new TaxDocumentValidationError(TaxDocumentValidationCode.Malformed, "kind"), result.Errors);
    }

    [Fact]
    public async Task IssueAsync_ShouldReturnTheNumber_WhenTheCallerRollsBack()
    {
        await using var tx = new InMemoryTaxDocumentStore.FakeTransaction();
        var result = await _flow.Service.IssueAsync(tx, Flow.Request());
        Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
        Assert.Equal(1, _flow.Numbers.Allocated);

        tx.Rollback();

        Assert.Equal(0, _flow.Numbers.Allocated);
        Assert.Empty(_flow.InMemory.Rows);
    }

    // ---- credit / debit note precondition ------------------------------------------------------------------------

    private IssueTaxDocumentRequest NoteFor(TaxDocumentRecord original, string seller = Flow.Seller)
    {
        var note = Samples.FullCancellationCreditNote();
        var snapshot = note with
        {
            Note = note.Note! with { OriginalDocumentId = original.Id, OriginalDocumentNumber = original.DocumentNumber },
        };
        return Flow.Request("refund_1", snapshot, seller);
    }

    private async Task<TaxDocumentRecord> WithOriginalPreparedAsync()
    {
        var doc = await _flow.IssuedAsync();
        await _flow.Sender.RunOnceAsync();
        return await _flow.GetAsync(doc.Id);
    }

    [Fact]
    public async Task IssueAsync_ShouldIssueACreditNote_WhenTheOriginalHasBeenPrepared()
    {
        var original = await WithOriginalPreparedAsync();

        var result = await _flow.IssueAsync(NoteFor(original));

        Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
    }

    [Fact]
    public async Task IssueAsync_ShouldRefuseACreditNote_WhenTheOriginalWasNeverPrepared()
    {
        var original = await _flow.IssuedAsync();

        var result = await _flow.IssueAsync(NoteFor(original));

        Assert.Equal(TaxDocumentIssueOutcome.NoteReferenceInvalid, result.Outcome);
        Assert.Equal(1, _flow.Numbers.Allocated);
    }

    [Fact]
    public async Task IssueAsync_ShouldRefuseANote_WhenTheOriginalIsCancelledOfAnotherSellerMissingOrMisnumbered()
    {
        var original = await WithOriginalPreparedAsync();

        var otherSeller = await _flow.IssueAsync(NoteFor(original, seller: "seller-2"));
        var missing = await _flow.IssueAsync(NoteFor(original with { Id = Guid.NewGuid() }));
        var misnumbered = await _flow.IssueAsync(NoteFor(original with { DocumentNumber = "TAX-9999" }) with { Key = new TaxDocumentKey(TaxDocumentKind.CreditNote, "refund_2") });
        Assert.Equal(TaxDocumentIssueOutcome.NoteReferenceInvalid, otherSeller.Outcome);
        Assert.Equal(TaxDocumentIssueOutcome.NoteReferenceInvalid, missing.Outcome);
        Assert.Equal(TaxDocumentIssueOutcome.NoteReferenceInvalid, misnumbered.Outcome);

        await CancelAsync(original.Id);
        var cancelled = await _flow.IssueAsync(NoteFor(original) with { Key = new TaxDocumentKey(TaxDocumentKind.CreditNote, "refund_3") });
        Assert.Equal(TaxDocumentIssueOutcome.NoteReferenceInvalid, cancelled.Outcome);
    }

    // ---- replacement ---------------------------------------------------------------------------------------------

    private IssueTaxDocumentRequest ReplacementFor(
        TaxDocumentRecord replaced, string source = "pay_1", string seller = Flow.Seller, int revision = 2) =>
        new()
        {
            Key = new TaxDocumentKey(TaxDocumentKind.TaxInvoiceReceipt, source, revision),
            SellerId = seller,
            Snapshot = Samples.Receipt() with
            {
                Replacement = new TaxDocumentReplacement
                {
                    ReplacesDocumentId = replaced.Id,
                    OriginalDocumentNumber = replaced.DocumentNumber,
                    OriginalIssueDate = replaced.IssueDate,
                    Reason = "Wrong buyer address",
                },
            },
        };

    [Fact]
    public async Task IssueAsync_ShouldCancelTheReplacedDocumentAndTakeANewNumber()
    {
        var old = await _flow.IssuedAsync();

        var result = await _flow.IssueAsync(ReplacementFor(old));

        Assert.Equal(TaxDocumentIssueOutcome.Issued, result.Outcome);
        var cancelled = await _flow.GetAsync(old.Id);
        Assert.Equal(TaxDocumentState.Cancelled, cancelled.State);
        Assert.Equal(result.DocumentId, cancelled.ReplacedByDocumentId);
        Assert.Equal("TAX-0001", cancelled.DocumentNumber);
        Assert.Equal("TAX-0002", (await _flow.GetAsync(result.DocumentId!.Value)).DocumentNumber);
    }

    [Fact]
    public async Task IssueAsync_ShouldRefuseTheReplacementAndConsumeNoNumber_WhileTheReplacedDocumentIsBeingSent()
    {
        var old = await _flow.IssuedAsync();
        await _flow.Store.ClaimDueAsync(10, _flow.Clock.GetUtcNow(), _flow.Options.Lease, Guid.NewGuid());

        var result = await _flow.IssueAsync(ReplacementFor(old));

        Assert.Equal(TaxDocumentIssueOutcome.ReplacedDocumentBusy, result.Outcome);
        Assert.Equal(1, _flow.Numbers.Allocated);
        Assert.Equal(TaxDocumentState.Issued, (await _flow.GetAsync(old.Id)).State);
    }

    [Fact]
    public async Task IssueAsync_ShouldRefuseTheReplacement_WhenTheReplacedDocumentIsMissingForeignMisnumberedOrAlreadyCancelled()
    {
        var old = await _flow.IssuedAsync();

        var missing = await _flow.IssueAsync(ReplacementFor(old with { Id = Guid.NewGuid() }));
        var foreign = await _flow.IssueAsync(ReplacementFor(old, seller: "seller-2"));
        var misnumbered = await _flow.IssueAsync(ReplacementFor(old with { DocumentNumber = "TAX-9999" }));
        Assert.All([missing, foreign, misnumbered], r => Assert.Equal(TaxDocumentIssueOutcome.ReplacementInvalid, r.Outcome));

        await CancelAsync(old.Id);
        var already = await _flow.IssueAsync(ReplacementFor(old));
        Assert.Equal(TaxDocumentIssueOutcome.ReplacementInvalid, already.Outcome);
        Assert.Equal(1, _flow.Numbers.Allocated);
    }

    [Fact]
    public async Task IssueAsync_ShouldRefuseAReplacementForADifferentSourceEvent()
    {
        var old = await _flow.IssuedAsync();

        var result = await _flow.IssueAsync(ReplacementFor(old, source: "pay_other"));

        Assert.Equal(TaxDocumentIssueOutcome.ReplacementInvalid, result.Outcome);
        Assert.Equal(TaxDocumentState.Issued, (await _flow.GetAsync(old.Id)).State);
        Assert.Equal(1, _flow.Numbers.Allocated);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task IssueAsync_ShouldRefuseAReplacementThatSkipsARevision(int revision)
    {
        var old = await _flow.IssuedAsync();

        var result = await _flow.IssueAsync(ReplacementFor(old, revision: revision));

        Assert.Equal(TaxDocumentIssueOutcome.ReplacementInvalid, result.Outcome);
        Assert.Equal(TaxDocumentState.Issued, (await _flow.GetAsync(old.Id)).State);
    }

    [Fact]
    public async Task IssueAsync_ShouldRefuseAReplacementOfADifferentKind()
    {
        var original = await WithOriginalPreparedAsync();
        var credit = await _flow.IssueAsync(NoteFor(original));
        var creditNote = await _flow.GetAsync(credit.DocumentId!.Value);

        // Same source event and next revision, but a tax invoice cannot replace a credit note.
        var result = await _flow.IssueAsync(ReplacementFor(creditNote, source: creditNote.SourceEventId));

        Assert.Equal(TaxDocumentIssueOutcome.ReplacementInvalid, result.Outcome);
        Assert.Equal(TaxDocumentState.Issued, (await _flow.GetAsync(creditNote.Id)).State);
    }

    [Fact]
    public async Task IssueAsync_ShouldReplaceTheLatestRevisionOfTheSameSource()
    {
        var old = await _flow.IssuedAsync();
        var second = await _flow.IssueAsync(ReplacementFor(old));
        var secondDoc = await _flow.GetAsync(second.DocumentId!.Value);

        var third = await _flow.IssueAsync(ReplacementFor(secondDoc, revision: 3));

        Assert.Equal(TaxDocumentIssueOutcome.Issued, third.Outcome);
        Assert.Equal(TaxDocumentState.Cancelled, (await _flow.GetAsync(secondDoc.Id)).State);
    }

    // ---- copies and requeue --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TaxDocumentState.Issued, "copy")]
    [InlineData(TaxDocumentState.Cancelled, "cancelled-copy")]
    public async Task RenderCopyAsync_ShouldNeverRenderAnOriginal(TaxDocumentState state, string marker)
    {
        var doc = await _flow.IssuedAsync();
        if (state == TaxDocumentState.Cancelled)
            await CancelAsync(doc.Id);

        var render = await _flow.Service.RenderCopyAsync(doc.Id, Flow.Seller);

        Assert.NotNull(render);
        Assert.Contains($"data-marker=\"{marker}\"", _flow.Pdf.Htmls[^1]);
    }

    [Fact]
    public async Task RenderCopyAsync_ShouldReturnNull_WhenThereIsNoSuchDocument() =>
        Assert.Null(await _flow.Service.RenderCopyAsync(Guid.NewGuid(), Flow.Seller));

    [Fact]
    public async Task RenderCopyAsync_ShouldAnswerExactlyLikeAMissingDocument_ForAnotherSeller()
    {
        var doc = await _flow.IssuedAsync();
        var htmlsBefore = _flow.Pdf.Htmls.Count;

        var foreign = await _flow.Service.RenderCopyAsync(doc.Id, "seller-2");
        var missing = await _flow.Service.RenderCopyAsync(Guid.NewGuid(), "seller-2");

        Assert.Null(foreign);
        Assert.Equal(missing, foreign);
        Assert.Equal(htmlsBefore, _flow.Pdf.Htmls.Count);
        Assert.NotNull(await _flow.Service.RenderCopyAsync(doc.Id, Flow.Seller));
    }

    [Fact]
    public async Task RequeueAsync_ShouldAnswerExactlyLikeAMissingDocument_ForAnotherSeller()
    {
        var doc = await _flow.IssuedAsync(buyerEmail: null);
        await _flow.Sender.RunOnceAsync();
        Assert.Equal(TaxDocumentState.Failed, (await _flow.GetAsync(doc.Id)).State);

        Assert.False(await _flow.Service.RequeueAsync(doc.Id, "seller-2", "operator-9"));
        Assert.False(await _flow.Service.RequeueAsync(Guid.NewGuid(), "seller-2", "operator-9"));

        Assert.Equal(TaxDocumentState.Failed, (await _flow.GetAsync(doc.Id)).State);
        Assert.DoesNotContain(_flow.Logs.Entries, e => e.Message.Contains("operator-9"));
    }

    [Fact]
    public async Task RequeueAsync_ShouldReturnAFailedDocumentToAutomationAndLogWhoDidIt()
    {
        var doc = await _flow.IssuedAsync(buyerEmail: null);
        await _flow.Sender.RunOnceAsync();
        Assert.Equal(TaxDocumentState.Failed, (await _flow.GetAsync(doc.Id)).State);

        Assert.True(await _flow.Service.RequeueAsync(doc.Id, Flow.Seller, "operator-7"));

        Assert.Equal(TaxDocumentState.Issued, (await _flow.GetAsync(doc.Id)).State);
        Assert.Contains(_flow.Logs.Entries, e => e.Message.Contains("requeued") && e.Message.Contains("operator-7"));
        Assert.False(await _flow.Service.RequeueAsync(doc.Id, Flow.Seller, "operator-7"));
    }

    private async Task CancelAsync(Guid id)
    {
        await using var tx = new InMemoryTaxDocumentStore.FakeTransaction();
        await _flow.Store.CancelForReplacementAsync(tx, id, Guid.NewGuid(), Flow.Seller, _flow.Clock.GetUtcNow() + TimeSpan.FromDays(1));
        tx.Commit();
    }
}
