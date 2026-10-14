using Xunit;

namespace Themia.ETax.Tests;

public class SnapshotValidatorTests
{
    private static IReadOnlyList<TaxDocumentValidationError> Validate(TaxDocumentSnapshot s, bool checksum = true) =>
        TaxDocumentSnapshotValidator.Validate(s, new TaxDocumentValidationOptions { ValidateTaxIdChecksum = checksum });

    private static void AssertSingle(IReadOnlyList<TaxDocumentValidationError> errors, TaxDocumentValidationCode code, string field)
    {
        var error = Assert.Single(errors);
        Assert.Equal(code, error.Code);
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void Validate_ShouldPass_ForAValidReceiptWithWithholding()
    {
        Assert.Empty(Validate(Samples.Receipt()));
    }

    [Fact]
    public void Validate_ShouldPass_ForFullCancellationCreditNote_AndAdjustmentNotes()
    {
        Assert.Empty(Validate(Samples.FullCancellationCreditNote()));
        Assert.Empty(Validate(Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 53_500)));
        Assert.Empty(Validate(Samples.AdjustmentNote(TaxDocumentKind.DebitNote, 107_000, 160_500)));
    }

    [Fact]
    public void Validate_ShouldPass_WhenBuyerHasNoTaxProfile_AndNoBranch()
    {
        var s = Samples.Receipt() with { Buyer = new TaxParty { Name = "ลูกค้า" } };

        Assert.Empty(Validate(s));
    }

    [Fact]
    public void Validate_ShouldRejectWithheldAboveTotal()
    {
        var s = Samples.Receipt();
        s = s with { Totals = s.Totals with { WithheldSatang = 200_000, NetReceivedSatang = -93_000 } };

        Assert.Contains(Validate(s), e => e.Code == TaxDocumentValidationCode.WithheldExceedsTotal);
    }

    [Fact]
    public void Validate_ShouldRejectNetReceivedThatIsNotTotalMinusWithheld()
    {
        var s = Samples.Receipt();
        s = s with { Totals = s.Totals with { NetReceivedSatang = 107_000 } };

        AssertSingle(Validate(s), TaxDocumentValidationCode.TotalsInconsistent, "totals.net_received_satang");
    }

    [Fact]
    public void Validate_ShouldRejectTotalsThatDisagreeWithVatMath()
    {
        var s = Samples.Receipt();
        s = s with { Totals = s.Totals with { VatSatang = 7_001, InclVatSatang = 107_001, NetReceivedSatang = 104_001 } };

        Assert.Contains(Validate(s), e => e.Code == TaxDocumentValidationCode.TotalsInconsistent);
    }

    [Fact]
    public void Validate_ShouldAcceptInclusiveDirectionTotals_WhenSplitOfInclusiveMatches()
    {
        // 100.01 baht inclusive at 7% -> ex 93.47, vat 6.54; AddVat(93.47) would give 100.01 too, but
        // the totals must be accepted whenever EITHER derivation reproduces them.
        var split = VatMath.Split(10_001, 700);
        var s = Samples.Receipt() with
        {
            Lines = [new TaxDocumentLine { Description = "x", AmountExVatSatang = split.ExVatSatang }],
            Totals = new TaxDocumentTotals
            {
                ExVatSatang = split.ExVatSatang, VatSatang = split.VatSatang, InclVatSatang = 10_001,
                WithheldSatang = 0, NetReceivedSatang = 10_001, VatRateBasisPoints = 700,
            },
        };

        Assert.Empty(Validate(s));
    }

    [Fact]
    public void Validate_ShouldRejectLinesThatDoNotSumToExVat()
    {
        var s = Samples.Receipt() with { Lines = [new TaxDocumentLine { Description = "x", AmountExVatSatang = 99_999 }] };

        AssertSingle(Validate(s), TaxDocumentValidationCode.TotalsInconsistent, "totals.ex_vat_satang");
    }

    [Fact]
    public void Validate_ShouldRejectNoLines_BlankDescription_NegativeAmount()
    {
        Assert.Contains(Validate(Samples.Receipt() with { Lines = [] }), e => e.Field == "lines" && e.Code == TaxDocumentValidationCode.Required);
        Assert.Contains(
            Validate(Samples.Receipt() with { Lines = [new TaxDocumentLine { Description = " ", AmountExVatSatang = 100_000 }] }),
            e => e.Field == "lines[0].description");
        Assert.Contains(
            Validate(Samples.Receipt() with { Lines = [new TaxDocumentLine { Description = "x", AmountExVatSatang = -1 }] }),
            e => e.Field == "lines[0].amount_ex_vat_satang");
    }

    [Fact]
    public void Validate_ShouldRequireSellerBranch_AndWellFormedTaxIds()
    {
        var s = Samples.Receipt();

        Assert.Contains(Validate(s with { Seller = s.Seller with { Branch = null } }), e => e.Field == "seller.branch");
        Assert.Contains(Validate(s with { Seller = s.Seller with { TaxId = "123" } }), e => e.Field == "seller.tax_id" && e.Code == TaxDocumentValidationCode.Malformed);
        Assert.Contains(Validate(s with { Buyer = s.Buyer with { Branch = "12" } }), e => e.Field == "buyer.branch");
    }

    [Fact]
    public void Validate_ShouldApplyChecksumOnlyWhenEnabled()
    {
        var s = Samples.Receipt();
        var bad = s with { Buyer = s.Buyer with { TaxId = "1101700230700" } };

        Assert.Contains(Validate(bad, checksum: true), e => e.Field == "buyer.tax_id" && e.Code == TaxDocumentValidationCode.InvalidChecksum);
        Assert.Empty(Validate(bad, checksum: false));
    }

    [Fact]
    public void Validate_ShouldDefaultChecksumOn()
    {
        var s = Samples.Receipt();
        var bad = s with { Buyer = s.Buyer with { TaxId = "1101700230700" } };

        Assert.NotEmpty(TaxDocumentSnapshotValidator.Validate(bad));
    }

    [Fact]
    public void Validate_ShouldRejectRateAbove100Percent_AndMissingNumber_AndDefaultDate()
    {
        var s = Samples.Receipt();

        Assert.Contains(Validate(s with { Totals = s.Totals with { VatRateBasisPoints = 10_001 } }), e => e.Field == "totals.vat_rate_basis_points");
        Assert.Contains(Validate(s with { DocumentNumber = " " }), e => e.Field == "document_number");
        Assert.Contains(Validate(s with { IssueDate = default }), e => e.Field == "issue_date");
    }

    [Fact]
    public void Validate_ShouldRequireNoteBlock_ForCreditAndDebitNotes_ButForbidItOnReceipts()
    {
        AssertSingle(Validate(Samples.Receipt() with { Kind = TaxDocumentKind.CreditNote }), TaxDocumentValidationCode.Required, "note");
        AssertSingle(Validate(Samples.FullCancellationCreditNote() with { Kind = TaxDocumentKind.TaxInvoiceReceipt }), TaxDocumentValidationCode.NotAllowed, "note");
    }

    [Fact]
    public void Validate_ShouldRejectDebitNoteFullCancellation()
    {
        AssertSingle(Validate(Samples.FullCancellationCreditNote() with { Kind = TaxDocumentKind.DebitNote }), TaxDocumentValidationCode.NotAllowed, "note.mode");
    }

    [Fact]
    public void Validate_ShouldRequireRefundedNetEqualToNetReceived_ForFullCancellation()
    {
        var s = Samples.FullCancellationCreditNote();
        var bad = s with { Note = s.Note! with { RefundedNetSatang = 107_000 } };

        AssertSingle(Validate(bad), TaxDocumentValidationCode.TotalsInconsistent, "note.refunded_net_satang");
        AssertSingle(Validate(s with { Note = s.Note! with { RefundedNetSatang = null } }), TaxDocumentValidationCode.Required, "note.refunded_net_satang");
    }

    [Fact]
    public void Validate_ShouldCheckAdjustmentDirection_AndDifference()
    {
        // a credit note must lower the value, a debit note raise it
        Assert.NotEmpty(Validate(Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 160_500)));
        Assert.NotEmpty(Validate(Samples.AdjustmentNote(TaxDocumentKind.DebitNote, 107_000, 53_500)));

        var s = Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 53_500);
        var wrongDiff = s with { Note = s.Note! with { CorrectedValueSatang = 50_000 } };
        Assert.Contains(Validate(wrongDiff), e => e.Field == "note.corrected_value_satang");
    }

    [Fact]
    public void Validate_ShouldRequireReasonAndOriginalReference_OnNotes()
    {
        var s = Samples.FullCancellationCreditNote();

        Assert.Contains(Validate(s with { Note = s.Note! with { Reason = "" } }), e => e.Field == "note.reason");
        Assert.Contains(Validate(s with { Note = s.Note! with { OriginalDocumentNumber = "" } }), e => e.Field == "note.original_document_number");
        Assert.Contains(Validate(s with { Note = s.Note! with { OriginalDocumentId = Guid.Empty } }), e => e.Field == "note.original_document_id");
    }

    [Fact]
    public void Validate_ShouldRequireReasonAndReference_OnReplacement()
    {
        var s = Samples.Receipt() with
        {
            Replacement = new TaxDocumentReplacement
            {
                ReplacesDocumentId = Guid.NewGuid(),
                OriginalDocumentNumber = "TAX-2026-000000",
                OriginalIssueDate = new DateOnly(2026, 10, 13),
                Reason = "Buyer name corrected",
            },
        };

        Assert.Empty(Validate(s));
        Assert.Contains(Validate(s with { Replacement = s.Replacement! with { Reason = " " } }), e => e.Field == "replacement.reason");
        Assert.Contains(Validate(s with { Replacement = s.Replacement! with { ReplacesDocumentId = Guid.Empty } }), e => e.Field == "replacement.replaces_document_id");
    }

    [Fact]
    public void Validate_ShouldNeverPutValuesInTheErrors()
    {
        var s = Samples.Receipt();
        var errors = Validate(s with { Buyer = s.Buyer with { TaxId = "1101700230700" } });

        Assert.DoesNotContain(errors, e => e.ToString().Contains("1101700230700"));
    }
    [Fact]
    public void Validate_ShouldReturnMalformed_NotThrow_WhenLineAmountsOverflowWhenSummed()
    {
        var s = Samples.Receipt() with
        {
            Lines =
            [
                new TaxDocumentLine { Description = "a", AmountExVatSatang = long.MaxValue },
                new TaxDocumentLine { Description = "b", AmountExVatSatang = long.MaxValue },
            ],
        };

        Assert.Contains(Validate(s), e => e.Code == TaxDocumentValidationCode.Malformed && e.Field == "totals");
    }

    [Fact]
    public void Validate_ShouldReturnMalformed_NotThrow_WhenExVatPlusVatOverflows()
    {
        var s = Samples.Receipt();
        s = s with
        {
            Lines = [new TaxDocumentLine { Description = "a", AmountExVatSatang = long.MaxValue }],
            Totals = s.Totals with { ExVatSatang = long.MaxValue, VatSatang = long.MaxValue },
        };

        Assert.Contains(Validate(s), e => e.Code == TaxDocumentValidationCode.Malformed && e.Field == "totals");
    }

    [Fact]
    public void Validate_ShouldReturnMalformed_NotThrow_WhenAddVatOverflows()
    {
        var ex = long.MaxValue - 10;
        var s = Samples.Receipt();
        s = s with
        {
            Lines = [new TaxDocumentLine { Description = "a", AmountExVatSatang = ex }],
            Totals = s.Totals with { ExVatSatang = ex, VatSatang = 7, InclVatSatang = ex + 7, WithheldSatang = 0, NetReceivedSatang = ex + 7 },
        };

        Assert.Contains(Validate(s), e => e.Code == TaxDocumentValidationCode.Malformed && e.Field == "totals");
    }

    [Fact]
    public void Validate_ShouldReturnMalformed_NotThrow_ForAdjustmentValuesAtLongExtremes()
    {
        var s = Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 53_500);
        var bad = s with { Note = s.Note! with { OriginalValueSatang = long.MinValue, CorrectedValueSatang = long.MaxValue } };

        Assert.Contains(Validate(bad), e => e.Field == "note.corrected_value_satang");
    }
}
