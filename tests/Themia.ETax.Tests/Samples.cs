using Themia.ETax;

namespace Themia.ETax.Tests;

internal static class Samples
{
    public const string SellerTaxId = "0105536078673";
    public const string BuyerTaxId = "1101700230708";

    public static TaxDocumentSnapshot Receipt() => new()
    {
        Kind = TaxDocumentKind.TaxInvoiceReceipt,
        DocumentNumber = "TAX-2026-000001",
        IssueDate = new DateOnly(2026, 10, 14),
        Seller = new TaxParty { Name = "บริษัท ตัวอย่าง จำกัด", TaxId = SellerTaxId, Branch = "00000", Address = "1 ถนนสุขุมวิท กรุงเทพมหานคร" },
        Buyer = new TaxParty { Name = "คุณสมชาย ใจดี", TaxId = BuyerTaxId, Branch = null, Address = null },
        Lines = [new TaxDocumentLine { Description = "Boost package", AmountExVatSatang = 100_000 }],
        Totals = new TaxDocumentTotals
        {
            ExVatSatang = 100_000,
            VatSatang = 7_000,
            InclVatSatang = 107_000,
            WithheldSatang = 3_000,
            NetReceivedSatang = 104_000,
            VatRateBasisPoints = 700,
        },
    };

    public static TaxDocumentSnapshot FullCancellationCreditNote() => Receipt() with
    {
        Kind = TaxDocumentKind.CreditNote,
        DocumentNumber = "CN-2026-000001",
        Note = new TaxNoteReference
        {
            OriginalDocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            OriginalDocumentNumber = "TAX-2026-000001",
            OriginalIssueDate = new DateOnly(2026, 10, 14),
            Mode = TaxNoteMode.FullCancellation,
            Reason = "Refund",
            RefundedNetSatang = 104_000,
        },
    };

    public static TaxDocumentSnapshot AdjustmentNote(TaxDocumentKind kind, long original, long corrected)
    {
        var diff = Math.Abs(original - corrected);
        var split = VatMath.Split(diff, 700);
        return Receipt() with
        {
            Kind = kind,
            DocumentNumber = "NOTE-2026-000001",
            Lines = [new TaxDocumentLine { Description = "Adjustment", AmountExVatSatang = split.ExVatSatang }],
            Totals = new TaxDocumentTotals
            {
                ExVatSatang = split.ExVatSatang,
                VatSatang = split.VatSatang,
                InclVatSatang = diff,
                WithheldSatang = 0,
                NetReceivedSatang = diff,
                VatRateBasisPoints = 700,
            },
            Note = new TaxNoteReference
            {
                OriginalDocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                OriginalDocumentNumber = "TAX-2026-000001",
                OriginalIssueDate = new DateOnly(2026, 10, 14),
                Mode = TaxNoteMode.Adjustment,
                Reason = "Price correction",
                OriginalValueSatang = original,
                CorrectedValueSatang = corrected,
            },
        };
    }
}
