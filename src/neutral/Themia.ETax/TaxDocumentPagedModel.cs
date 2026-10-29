using System.Globalization;

namespace Themia.ETax;

/// <summary>The data the paged template (versions "4" A4 portrait and "5" A5 landscape) prints: one entry per page, split by <see cref="TaxDocumentPageLayout"/>.</summary>
internal static class TaxDocumentPagedModel
{
    private const string HeadOfficeBranch = "00000";
    private static readonly int[] TaxIdGroups = [1, 4, 5, 2, 1];

    public static object Build(
        TaxDocumentSnapshot s, TaxDocumentMarker marker, string fontCss, ThaiBahtTextEtMode etMode, TaxDocumentLogo? logo, TaxDocumentPageSize size)
    {
        var isReceipt = s.Kind == TaxDocumentKind.TaxInvoiceReceipt;
        var isCredit = s.Kind == TaxDocumentKind.CreditNote;
        var plan = TaxDocumentPageLayout.Plan(size, s.Kind, s.Lines.Count);
        var (titleTh, titleEn) = Title(s.Kind);
        var (markerTh, markerEn) = marker == TaxDocumentMarker.Original ? ("ต้นฉบับ", "Original") : ("สำเนา", "Copy");

        return new
        {
            fontCss,
            landscape = size == TaxDocumentPageSize.A5Landscape,
            documentNumber = s.DocumentNumber,
            issueDate = TaxDocumentRenderModel.Date(s.IssueDate),
            logo = logo?.ToDataUri(),
            cancelled = marker == TaxDocumentMarker.CancelledCopy,
            receipt = isReceipt,
            credit = isCredit,
            debit = s.Kind == TaxDocumentKind.DebitNote,
            note = s.Kind != TaxDocumentKind.TaxInvoiceReceipt,
            titleTh,
            titleEn,
            markerTh,
            markerEn,
            seller = Party(s.Seller),
            buyer = Party(s.Buyer),
            original = s.Note is { } n ? new { number = n.OriginalDocumentNumber, date = TaxDocumentRenderModel.Date(n.OriginalIssueDate) } : null,
            replacement = s.Replacement is { } r
                ? new { number = r.OriginalDocumentNumber, date = TaxDocumentRenderModel.Date(r.OriginalIssueDate) }
                : null,
            payment = isReceipt ? Payment(s.Payment) : null,
            reasons = isCredit ? Reasons(s.Note) : null,
            reasonText = s.Note?.Reason,
            totals = Totals(s),
            amountInWords = ThaiBahtText.Format(s.Totals.InclVatSatang, etMode),
            pages = Pages(s, plan, size),
        };
    }

    private static object[] Pages(TaxDocumentSnapshot s, IReadOnlyList<TaxDocumentPagePlan> plan, TaxDocumentPageSize size)
    {
        var pages = new object[plan.Count];
        var carried = new Sums();
        for (var i = 0; i < plan.Count; i++)
        {
            var page = plan[i];
            var broughtForward = i == 0 ? null : carried.Show(s.Kind);
            var rows = new List<object>();
            for (var l = page.Start; l < page.Start + page.Count; l++)
            {
                rows.Add(Row(s.Kind, s.Lines[l], l + 1));
                carried.Add(s.Lines[l]);
            }

            // A small form keeps its table at the form's full height; A4 flows from the top and is not padded.
            if (page.Role == TaxDocumentPageRole.Single)
            {
                for (var pad = page.Count; pad < TaxDocumentPageLayout.PaddedRows(size, s.Kind); pad++)
                    rows.Add(new { blank = true });
            }

            pages[i] = new
            {
                number = i + 1,
                count = plan.Count,
                full = page.Role is TaxDocumentPageRole.Single or TaxDocumentPageRole.First,
                continued = page.Role is TaxDocumentPageRole.Middle or TaxDocumentPageRole.Last,
                multi = plan.Count > 1,
                closing = page.Role is TaxDocumentPageRole.Single or TaxDocumentPageRole.Last,
                broughtForward,
                carriedForward = page.Role is TaxDocumentPageRole.First or TaxDocumentPageRole.Middle ? carried.Show(s.Kind) : null,
                rows,
            };
        }

        return pages;
    }

    private static object Row(TaxDocumentKind kind, TaxDocumentLine l, int number) => new
    {
        blank = false,
        number = number.ToString(CultureInfo.InvariantCulture),
        description = l.Description,
        quantity = l.Quantity is { } q ? Quantity(q, l.Unit) : null,
        unitPrice = l.UnitPriceSatang is { } p ? TaxDocumentRenderModel.Money(p) : null,
        amount = TaxDocumentRenderModel.Money(l.AmountExVatSatang),
        original = l.OriginalAmountExVatSatang is { } o ? TaxDocumentRenderModel.Money(o) : null,
        correct = l.CorrectAmountExVatSatang is { } c ? TaxDocumentRenderModel.Money(c) : null,
        difference = kind == TaxDocumentKind.TaxInvoiceReceipt ? null : TaxDocumentRenderModel.Money(l.AmountExVatSatang),
    };

    private static string Quantity(decimal quantity, string? unit)
    {
        var text = quantity.ToString("0.####", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(unit) ? text : text + " " + unit;
    }

    private static object Totals(TaxDocumentSnapshot s) => new
    {
        subtotal = TaxDocumentRenderModel.Money(s.Totals.ExVatSatang),
        vat = TaxDocumentRenderModel.Money(s.Totals.VatSatang),
        total = TaxDocumentRenderModel.Money(s.Totals.InclVatSatang),
        rate = (s.Totals.VatRateBasisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%",
        withheld = s.Kind == TaxDocumentKind.TaxInvoiceReceipt && s.Totals.WithheldSatang > 0 ? TaxDocumentRenderModel.Money(s.Totals.WithheldSatang) : null,
        netReceived = s.Kind == TaxDocumentKind.TaxInvoiceReceipt && s.Totals.WithheldSatang > 0 ? TaxDocumentRenderModel.Money(s.Totals.NetReceivedSatang) : null,
    };

    private static object? Payment(TaxPayment? p) => p is null
        ? null
        : new
        {
            cash = p.Method == TaxPaymentMethod.Cash,
            transfer = p.Method == TaxPaymentMethod.Transfer,
            card = p.Method == TaxPaymentMethod.Card,
            mobileBanking = p.Method == TaxPaymentMethod.MobileBanking,
            wallet = p.Method == TaxPaymentMethod.Wallet,
            date = p.Date is { } d ? TaxDocumentRenderModel.Date(d) : null,
        };

    private static object[] Reasons(TaxNoteReference? note) =>
    [
        Reason(note, TaxNoteReason.PriceReduction, "ลดราคาสินค้า/บริการ", "Price reduction"),
        Reason(note, TaxNoteReason.GoodsReturned, "รับคืนสินค้า", "Goods returned"),
        Reason(note, TaxNoteReason.Compensation, "ค่าชดเชย", "Compensation"),
        Reason(note, TaxNoteReason.DepositRefund, "คืนเงินมัดจำ/เงินล่วงหน้า", "Deposit/advance refund"),
        Reason(note, TaxNoteReason.ServiceCancelled, "ยกเลิกสัญญาบริการ", "Service contract cancelled"),
    ];

    private static object Reason(TaxNoteReference? note, TaxNoteReason reason, string th, string en) => new { th, en, on = note?.ReasonKind == reason };

    private static object Party(TaxParty p) => new
    {
        name = p.Name,
        address = p.Address,
        taxId = p.TaxId is null ? null : TaxId(p.TaxId),
        branchTh = p.Branch is null ? null : p.Branch == HeadOfficeBranch ? "สำนักงานใหญ่" : "สาขาที่ " + p.Branch,
        branchEn = p.Branch is null ? null : p.Branch == HeadOfficeBranch ? "Head office" : "Branch " + p.Branch,
    };

    // A 13-digit id prints grouped the way the Revenue Department writes it (1 4 5 2 1); anything else prints as given.
    private static string TaxId(string id)
    {
        if (id.Length != 13 || !id.All(char.IsAsciiDigit))
            return id;

        var parts = new List<string>(TaxIdGroups.Length);
        var at = 0;
        foreach (var size in TaxIdGroups)
        {
            parts.Add(id.Substring(at, size));
            at += size;
        }

        return string.Join(' ', parts);
    }

    private static (string Th, string En) Title(TaxDocumentKind kind) => kind switch
    {
        TaxDocumentKind.TaxInvoiceReceipt => ("ใบเสร็จรับเงิน/ใบกำกับภาษี", "Receipt/Tax Invoice"),
        TaxDocumentKind.CreditNote => ("ใบลดหนี้", "Credit Note"),
        TaxDocumentKind.DebitNote => ("ใบเพิ่มหนี้", "Debit Note"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown tax document kind."),
    };

    // The running subtotal of the columns a page carries forward. Satang in a long: a document's lines never come near its range.
    private sealed class Sums
    {
        private long _amount;
        private long _original;
        private long _correct;
        private bool _hasValues = true;

        public void Add(TaxDocumentLine l)
        {
            _amount = checked(_amount + l.AmountExVatSatang);
            if (l.OriginalAmountExVatSatang is { } o && l.CorrectAmountExVatSatang is { } c)
            {
                _original = checked(_original + o);
                _correct = checked(_correct + c);
            }
            else
            {
                _hasValues = false;
            }
        }

        public object Show(TaxDocumentKind kind) => new
        {
            amount = TaxDocumentRenderModel.Money(_amount),
            original = kind != TaxDocumentKind.TaxInvoiceReceipt && _hasValues ? TaxDocumentRenderModel.Money(_original) : null,
            correct = kind != TaxDocumentKind.TaxInvoiceReceipt && _hasValues ? TaxDocumentRenderModel.Money(_correct) : null,
        };
    }
}
