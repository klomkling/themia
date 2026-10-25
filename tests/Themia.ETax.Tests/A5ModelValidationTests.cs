using Xunit;

namespace Themia.ETax.Tests;

public sealed class A5ModelValidationTests
{
    private static IReadOnlyList<TaxDocumentValidationError> Validate(TaxDocumentSnapshot s) =>
        TaxDocumentSnapshotValidator.Validate(s, new TaxDocumentValidationOptions { ValidateTaxIdChecksum = true });

    private static void AssertSingle(IReadOnlyList<TaxDocumentValidationError> errors, TaxDocumentValidationCode code, string field)
    {
        var error = Assert.Single(errors);
        Assert.Equal(code, error.Code);
        Assert.Equal(field, error.Field);
    }

    private static TaxDocumentSnapshot ReceiptWith(TaxDocumentLine line) => Samples.Receipt() with { Lines = [line] };

    private static TaxDocumentLine Line() => new() { Description = "Boost package", AmountExVatSatang = 100_000 };

    // ---- receipt lines and payment ---------------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldAcceptQuantityUnitAndUnitPrice_OnAReceiptLine() =>
        Assert.Empty(Validate(ReceiptWith(Line() with { Quantity = 2m, Unit = "ชิ้น", UnitPriceSatang = 50_000 })));

    [Fact]
    public void Validate_ShouldRequireAUnitPrice_WhenAQuantityIsGiven_AndTheOtherWayRound()
    {
        AssertSingle(Validate(ReceiptWith(Line() with { Quantity = 2m })), TaxDocumentValidationCode.Required, "lines[0].unit_price_satang");
        AssertSingle(Validate(ReceiptWith(Line() with { UnitPriceSatang = 50_000 })), TaxDocumentValidationCode.Required, "lines[0].quantity");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldRejectANonPositiveQuantity(int quantity) =>
        AssertSingle(Validate(ReceiptWith(Line() with { Quantity = quantity, UnitPriceSatang = 50_000 })), TaxDocumentValidationCode.Malformed, "lines[0].quantity");

    [Fact]
    public void Validate_ShouldRejectANegativeUnitPrice() =>
        AssertSingle(Validate(ReceiptWith(Line() with { Quantity = 1m, UnitPriceSatang = -1 })), TaxDocumentValidationCode.Malformed, "lines[0].unit_price_satang");

    [Fact]
    public void Validate_ShouldAcceptAPaymentOnAReceipt_AndRejectItOnACreditNote()
    {
        var payment = new TaxPayment { Method = TaxPaymentMethod.Transfer, Date = new DateOnly(2026, 10, 14) };

        Assert.Empty(Validate(Samples.Receipt() with { Payment = payment }));
        AssertSingle(Validate(Samples.FullCancellationCreditNote() with { Payment = payment }), TaxDocumentValidationCode.NotAllowed, "payment");
    }

    [Fact]
    public void Validate_ShouldRejectAnUnknownPaymentMethod() =>
        AssertSingle(Validate(Samples.Receipt() with { Payment = new TaxPayment { Method = (TaxPaymentMethod)9 } }), TaxDocumentValidationCode.Malformed, "payment.method");

    // ---- credit note lines -----------------------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldAcceptPerLineOriginalAndCorrectValues_OnACreditNote()
    {
        var note = Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 53_500);
        var line = note.Lines[0] with { OriginalAmountExVatSatang = 100_000, CorrectAmountExVatSatang = 50_000 };

        Assert.Empty(Validate(note with { Lines = [line] }));
    }

    [Fact]
    public void Validate_ShouldRejectPerLineOriginalAndCorrectValues_OnAReceipt() =>
        AssertSingle(
            Validate(ReceiptWith(Line() with { OriginalAmountExVatSatang = 100_000, CorrectAmountExVatSatang = 50_000 })),
            TaxDocumentValidationCode.NotAllowed, "lines[0].original_amount_ex_vat_satang");

    [Fact]
    public void Validate_ShouldRequireBothPerLineValues_OrNeither()
    {
        var note = Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 53_500);

        AssertSingle(
            Validate(note with { Lines = [note.Lines[0] with { OriginalAmountExVatSatang = 100_000 }] }),
            TaxDocumentValidationCode.Required, "lines[0].correct_amount_ex_vat_satang");
    }

    // ---- note reason -----------------------------------------------------------------------------------------------

    [Fact]
    public void Validate_ShouldRequireAReasonKind_OnANewCreditNote()
    {
        var s = Samples.AdjustmentNote(TaxDocumentKind.CreditNote, 107_000, 53_500);

        AssertSingle(Validate(s with { Note = s.Note! with { ReasonKind = null } }), TaxDocumentValidationCode.Required, "note.reason_kind");
    }

    [Fact]
    public void Validate_ShouldRejectAReasonKindOnADebitNote()
    {
        var s = Samples.AdjustmentNote(TaxDocumentKind.DebitNote, 107_000, 160_500);

        AssertSingle(Validate(s with { Note = s.Note! with { ReasonKind = TaxNoteReason.PriceReduction } }), TaxDocumentValidationCode.NotAllowed, "note.reason_kind");
    }

    [Fact]
    public void Validate_ShouldRejectAnUnknownReasonKind()
    {
        var s = Samples.FullCancellationCreditNote();

        AssertSingle(Validate(s with { Note = s.Note! with { ReasonKind = (TaxNoteReason)99 } }), TaxDocumentValidationCode.Malformed, "note.reason_kind");
    }

    [Fact]
    public void Validate_ShouldAcceptAReasonCode_ThatAgreesWithTheReasonKind()
    {
        var s = Samples.FullCancellationCreditNote();

        Assert.Empty(Validate(s with { Note = s.Note! with { ReasonKind = TaxNoteReason.GoodsReturned, ReasonCode = "goods_returned" } }));
    }

    [Fact]
    public void Validate_ShouldRejectAReasonCode_ThatDisagreesWithTheReasonKind()
    {
        var s = Samples.FullCancellationCreditNote();

        AssertSingle(
            Validate(s with { Note = s.Note! with { ReasonKind = TaxNoteReason.GoodsReturned, ReasonCode = "price_reduction" } }),
            TaxDocumentValidationCode.Malformed, "note.reason_code");
    }

    [Theory]
    [InlineData(TaxNoteReason.PriceReduction, "price_reduction")]
    [InlineData(TaxNoteReason.GoodsReturned, "goods_returned")]
    [InlineData(TaxNoteReason.Compensation, "compensation")]
    [InlineData(TaxNoteReason.DepositRefund, "deposit_refund")]
    [InlineData(TaxNoteReason.ServiceCancelled, "service_cancelled")]
    public void CodeOf_ShouldGiveEachReasonItsStableCode(TaxNoteReason reason, string code) =>
        Assert.Equal(code, TaxNoteReasons.CodeOf(reason));

    // ---- serialisation ---------------------------------------------------------------------------------------------

    [Fact]
    public void Serialize_ShouldWriteTheNewFieldsWhenSet_AndRoundTripThem()
    {
        var s = Samples.Receipt() with
        {
            Lines = [Line() with { Quantity = 2m, Unit = "ชิ้น", UnitPriceSatang = 50_000 }],
            Payment = new TaxPayment { Method = TaxPaymentMethod.Cash, Date = new DateOnly(2026, 10, 14) },
        };

        var json = TaxDocumentSnapshotSerializer.Serialize(s);
        var back = TaxDocumentSnapshotSerializer.Deserialize(json, TaxDocumentSnapshotSerializer.ComputeHash(json));

        Assert.Contains("\"quantity\":2", json);
        Assert.Contains("\"unit_price_satang\":50000", json);
        Assert.Contains("\"payment\":{\"method\":\"Cash\",\"date\":\"2026-10-14\"}", json);
        Assert.Equal(json, TaxDocumentSnapshotSerializer.Serialize(back));
        Assert.Equal(2m, back.Lines[0].Quantity);
        Assert.Equal(TaxPaymentMethod.Cash, back.Payment!.Method);
    }
}
