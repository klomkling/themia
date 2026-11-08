using System.Globalization;
using System.Text;

namespace Themia.ETax;

/// <summary>Turns a snapshot into the plain, pre-formatted values the template prints. Pure: no clock, no culture.</summary>
internal static class TaxDocumentRenderModel
{
    private const int BuddhistEraOffset = 543;

    public static object Build(TaxDocumentSnapshot s, TaxDocumentMarker marker, string fontCss, ThaiBahtTextEtMode etMode)
    {
        var (titleTh, titleEn) = Title(s.Kind, s.Form);
        var (markerTh, markerEn, markerKey) = Mark(marker);
        return new
        {
            fontCss,
            documentNumber = s.DocumentNumber,
            titleTh,
            titleEn,
            marker = markerKey,
            markerTh,
            markerEn,
            cancelled = marker == TaxDocumentMarker.CancelledCopy,
            issueDate = Date(s.IssueDate),
            seller = Party(s.Seller),
            buyer = Party(s.Buyer),
            lines = s.Lines.Select((l, i) => new
            {
                number = (i + 1).ToString(CultureInfo.InvariantCulture),
                description = l.Description,
                quantity = l.Quantity is { } q ? TaxDocumentPagedModel.Quantity(q, l.Unit) : null,
                amount = Money(l.AmountExVatSatang),
            }).ToArray(),
            totals = new
            {
                exVat = Money(s.Totals.ExVatSatang),
                vat = Money(s.Totals.VatSatang),
                inclVat = Money(s.Totals.InclVatSatang),
                rate = Rate(s.Totals.VatRateBasisPoints),
                withheld = s.Totals.WithheldSatang > 0 ? Money(s.Totals.WithheldSatang) : null,
                netReceived = Money(s.Totals.NetReceivedSatang),
            },
            // The document total: the grand total including VAT; for a credit or debit note, the amount credited or debited. Printed by template 2 only.
            amountInWords = ThaiBahtText.Format(s.Totals.InclVatSatang, etMode),
            note = s.Note is { } n ? Note(s.Kind, n) : null,
            replacement = s.Replacement is { } r
                ? new { originalNumber = r.OriginalDocumentNumber, originalDate = Date(r.OriginalIssueDate), reason = r.Reason }
                : null,
        };
    }

    public static string Money(long satang)
    {
        var abs = satang < 0 ? -(decimal)satang : satang;
        var baht = Math.Floor(abs / 100m);
        var text = string.Create(CultureInfo.InvariantCulture, $"{baht:N0}.{abs % 100m:00}");
        return satang < 0 ? "-" + text : text;
    }

    // Buddhist-era year (Gregorian + 543), computed here on purpose: th-TH culture or ThaiBuddhistCalendar would read
    // ICU/OS data that can differ between a macOS host and a Linux container and break byte-stable renders.
    internal static string Date(DateOnly d) => string.Create(
        CultureInfo.InvariantCulture, $"{d.Day:00}/{d.Month:00}/{d.Year + BuddhistEraOffset}");

    private static string Rate(int basisPoints) =>
        (basisPoints / 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static object Party(TaxParty p) => new
    {
        name = p.Name,
        address = p.Address,
        taxId = p.TaxId,
        branch = TaxPartyText.FlatBranch(p.Branch),
    };

    private static object Note(TaxDocumentKind kind, TaxNoteReference n) => new
    {
        heading = kind == TaxDocumentKind.CreditNote ? "รายละเอียดการลดหนี้ / Credit note details" : "รายละเอียดการเพิ่มหนี้ / Debit note details",
        originalNumber = n.OriginalDocumentNumber,
        originalDate = Date(n.OriginalIssueDate),
        reasonCode = n.ReasonCode,
        reason = n.Reason,
        refundedNet = n.RefundedNetSatang is { } r ? Money(r) : null,
        originalValue = n.OriginalValueSatang is { } o ? Money(o) : null,
        correctedValue = n.CorrectedValueSatang is { } c ? Money(c) : null,
    };

    private static (string Th, string En) Title(TaxDocumentKind kind, TaxDocumentForm form) => kind switch
    {
        TaxDocumentKind.TaxInvoiceReceipt when form == TaxDocumentForm.Abbreviated => ("ใบกำกับภาษีอย่างย่อ", ""),
        TaxDocumentKind.TaxInvoiceReceipt => ("ใบกำกับภาษี/ใบเสร็จรับเงิน", "Tax Invoice / Receipt"),
        TaxDocumentKind.CreditNote => ("ใบลดหนี้", "Credit Note"),
        TaxDocumentKind.DebitNote => ("ใบเพิ่มหนี้", "Debit Note"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown tax document kind."),
    };

    private static (string Th, string En, string Key) Mark(TaxDocumentMarker marker) => marker switch
    {
        TaxDocumentMarker.Original => ("ต้นฉบับ", "ORIGINAL", "original"),
        TaxDocumentMarker.Copy => ("สำเนา", "COPY", "copy"),
        TaxDocumentMarker.CancelledCopy => ("สำเนา", "COPY", "cancelled-copy"),
        _ => throw new ArgumentOutOfRangeException(nameof(marker), marker, "Unknown marker."),
    };

    public static string FontFaceCss()
    {
        var sb = new StringBuilder();
        AppendFace(sb, TaxDocumentFonts.Regular, 400);
        AppendFace(sb, TaxDocumentFonts.Bold, 700);
        return sb.ToString();
    }

    private static void AppendFace(StringBuilder sb, byte[] font, int weight) =>
        sb.Append("@font-face { font-family: 'Sarabun'; font-style: normal; font-weight: ")
          .Append(weight.ToString(CultureInfo.InvariantCulture))
          .Append("; src: url(data:font/ttf;base64,").Append(Convert.ToBase64String(font)).Append(") format('truetype'); }\n");
}
