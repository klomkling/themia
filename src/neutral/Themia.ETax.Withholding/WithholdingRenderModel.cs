using System.Globalization;

namespace Themia.ETax.Withholding;

/// <summary>
/// Turns a snapshot into the plain, pre-formatted values the template prints. Pure: no clock, no culture. Every label here is printed on the
/// certificate, so a change to this file changes what template version "1" prints: ship template "2" instead (the golden tests say so).
/// </summary>
internal static class WithholdingRenderModel
{
    private const int BuddhistEraOffset = 543;
    private static readonly int[] TaxIdGroups = [1, 4, 5, 2, 1];
    private static readonly int[] LegacyTaxIdGroups = [1, 4, 4, 1];

    // The seven filing forms in the order, numbering and wording of the official certificate.
    private static readonly (WithholdingFilingForm Form, string Number, string Label)[] FilingForms =
    [
        (WithholdingFilingForm.Pnd1A, "1", "ภ.ง.ด.1ก"),
        (WithholdingFilingForm.Pnd1ASpecial, "2", "ภ.ง.ด.1ก พิเศษ"),
        (WithholdingFilingForm.Pnd2, "3", "ภ.ง.ด.2"),
        (WithholdingFilingForm.Pnd3, "4", "ภ.ง.ด.3"),
        (WithholdingFilingForm.Pnd2A, "5", "ภ.ง.ด.2ก"),
        (WithholdingFilingForm.Pnd3A, "6", "ภ.ง.ด.3ก"),
        (WithholdingFilingForm.Pnd53, "7", "ภ.ง.ด.53"),
    ];

    // The income table of the official certificate, line by line. A null row is a heading with no amount; Specify marks the lines that
    // print what the payer writes on a dotted line ("(ระบุ)").
    private static readonly (WithholdingFormRow? Row, int Indent, string Label, bool Specify)[] Layout =
    [
        (WithholdingFormRow.Salary40_1, 0, "1. เงินเดือน ค่าจ้าง เบี้ยเลี้ยง โบนัส ฯลฯ ตามมาตรา 40 (1)", false),
        (WithholdingFormRow.Fees40_2, 0, "2. ค่าธรรมเนียม ค่านายหน้า ฯลฯ ตามมาตรา 40 (2)", false),
        (WithholdingFormRow.Royalty40_3, 0, "3. ค่าแห่งลิขสิทธิ์ ฯลฯ ตามมาตรา 40 (3)", false),
        (WithholdingFormRow.Interest40_4a, 0, "4. (ก) ดอกเบี้ย ฯลฯ ตามมาตรา 40 (4) (ก)", false),
        (null, 1, "(ข) เงินปันผล เงินส่วนแบ่งกำไร ฯลฯ ตามมาตรา 40 (4) (ข)", false),
        (null, 2, "(1) กรณีผู้ได้รับเงินปันผลได้รับเครดิตภาษี โดยจ่ายจากกำไรสุทธิของกิจการที่ต้องเสียภาษีเงินได้นิติบุคคลในอัตราดังนี้", false),
        (WithholdingFormRow.DividendCredit30, 3, "(1.1) อัตราร้อยละ 30 ของกำไรสุทธิ", false),
        (WithholdingFormRow.DividendCredit25, 3, "(1.2) อัตราร้อยละ 25 ของกำไรสุทธิ", false),
        (WithholdingFormRow.DividendCredit20, 3, "(1.3) อัตราร้อยละ 20 ของกำไรสุทธิ", false),
        (WithholdingFormRow.DividendCreditOtherRate, 3, "(1.4) อัตราอื่น ๆ (ระบุ)", true),
        (null, 2, "(2) กรณีผู้ได้รับเงินปันผลไม่ได้รับเครดิตภาษี เนื่องจากจ่ายจาก", false),
        (WithholdingFormRow.DividendNoCreditExemptProfit, 3, "(2.1) กำไรสุทธิของกิจการที่ได้รับยกเว้นภาษีเงินได้นิติบุคคล", false),
        (WithholdingFormRow.DividendNoCreditExemptShare, 3, "(2.2) เงินปันผลหรือเงินส่วนแบ่งของกำไรที่ได้รับยกเว้นไม่ต้องนำมารวมคำนวณเป็นรายได้เพื่อเสียภาษีเงินได้นิติบุคคล", false),
        (WithholdingFormRow.DividendNoCreditLossCarriedForward, 3, "(2.3) กำไรสุทธิส่วนที่ได้หักผลขาดทุนสุทธิยกมาไม่เกิน 5 ปี ก่อนรอบระยะเวลาบัญชีปีปัจจุบัน", false),
        (WithholdingFormRow.DividendNoCreditEquityMethod, 3, "(2.4) กำไรที่รับรู้ทางบัญชีโดยวิธีส่วนได้เสีย (equity method)", false),
        (WithholdingFormRow.DividendNoCreditOther, 3, "(2.5) อื่น ๆ (ระบุ)", true),
        (WithholdingFormRow.Section3Tredecim, 0, "5. การจ่ายเงินได้ที่ต้องหักภาษี ณ ที่จ่าย ตามคำสั่งกรมสรรพากรที่ออกตามมาตรา 3 เตรส เช่น รางวัล ส่วนลดหรือประโยชน์ใด ๆ เนื่องจากการส่งเสริมการขาย รางวัลในการประกวด การแข่งขัน การชิงโชค ค่าแสดงของนักแสดงสาธารณะ ค่าจ้างทำของ ค่าโฆษณา ค่าเช่า ค่าขนส่ง ค่าบริการ ค่าเบี้ยประกันวินาศภัย ฯลฯ", true),
        (WithholdingFormRow.Other, 0, "6. อื่น ๆ (ระบุ)", true),
    ];

    // Fixed for the certificate (หนึ่งร้อยหนึ่ง, not หนึ่งร้อยเอ็ด), not an adopter option: an option read at render time would change the bytes of
    // issued certificates when it changed. Any other convention ships as a new template version.
    private const ThaiBahtTextEtMode WordsEtMode = ThaiBahtTextEtMode.TensOnly;

    public static object Build(WithholdingCertificateSnapshot s, WithholdingCopy copy, WithholdingRenderMode mode, string fontCss)
    {
        var byRow = new Dictionary<WithholdingFormRow, WithholdingIncomeRow>();
        foreach (var row in s.Rows)
            byRow.TryAdd(row.FormRow, row);

        return new
        {
            fontCss,
            documentNumber = s.DocumentNumber,
            bookNumber = s.BookNumber,
            copy = copy == WithholdingCopy.Copy1 ? "copy-1" : "copy-2",
            copy1 = copy == WithholdingCopy.Copy1,
            copy2 = copy == WithholdingCopy.Copy2,
            replacementCopy = mode == WithholdingRenderMode.ReplacementCopy,
            cancelled = mode == WithholdingRenderMode.Cancelled,
            payer = Party(s.Payer, s.PayerKind, s.PayerLegacyTaxId),
            payee = Party(s.Payee, s.PayeeKind, s.PayeeLegacyTaxId),
            filingSequence = s.FilingSequence?.ToString(CultureInfo.InvariantCulture),
            filingForms = FilingForms.Select(f => new { number = f.Number, label = f.Label, on = f.Form == s.FilingForm }).ToArray(),
            lines = Layout.Select(l => Line(l.Row, l.Indent, l.Label, l.Specify, l.Row is { } r && byRow.TryGetValue(r, out var row) ? row : null)).ToArray(),
            totals = new
            {
                paid = Split(s.Totals.PaidSatang),
                withheld = Split(s.Totals.WithheldSatang),
                withheldInWords = ThaiBahtText.Format(s.Totals.WithheldSatang, WordsEtMode),
            },
            funds = new
            {
                governmentPension = Amount(s.Funds?.GovernmentPensionSatang),
                socialSecurity = Amount(s.Funds?.SocialSecuritySatang),
                providentFund = Amount(s.Funds?.ProvidentFundSatang),
            },
            conditions = new[]
            {
                Condition("1", "หัก ณ ที่จ่าย", s.Condition == WithholdingCondition.WithheldAtSource, null),
                Condition("2", "ออกให้ตลอดไป", s.Condition == WithholdingCondition.PaidOnBehalfForever, null),
                Condition("3", "ออกให้ครั้งเดียว", s.Condition == WithholdingCondition.PaidOnBehalfOnce, null),
                Condition("4", "อื่น ๆ (ระบุ)", s.Condition == WithholdingCondition.Other, s.ConditionOther),
            },
            issued = new
            {
                day = s.IssueDate.Day.ToString("00", CultureInfo.InvariantCulture),
                month = s.IssueDate.Month.ToString("00", CultureInfo.InvariantCulture),
                year = (s.IssueDate.Year + BuddhistEraOffset).ToString("0000", CultureInfo.InvariantCulture),
            },
        };
    }

    internal static string KindLabel(WithholdingPartyKind kind) => kind switch
    {
        WithholdingPartyKind.Individual => "บุคคล",
        WithholdingPartyKind.JuristicPerson => "นิติบุคคล",
        WithholdingPartyKind.Company => "บริษัท",
        WithholdingPartyKind.Association => "สมาคม",
        WithholdingPartyKind.BodyOfPersons => "คณะบุคคล",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown party kind."),
    };

    private static object Party(TaxParty p, WithholdingPartyKind kind, string? legacyTaxId) => new
    {
        name = p.Name,
        kind = KindLabel(kind),
        address = p.Address,
        taxId = Groups(p.TaxId, TaxIdGroups),
        legacyTaxId = Groups(legacyTaxId, LegacyTaxIdGroups),
    };

    // The digits of an id spread over the printed boxes, grouped as the form groups them. A missing id leaves empty boxes.
    private static object[] Groups(string? digits, int[] groups)
    {
        var at = 0;
        return groups.Select(size => new
        {
            cells = Enumerable.Range(0, size).Select(_ =>
            {
                var cell = digits is not null && at < digits.Length ? digits[at].ToString() : string.Empty;
                at++;
                return cell;
            }).ToArray(),
        }).ToArray<object>();
    }

    private static object Line(WithholdingFormRow? formRow, int indent, string label, bool specify, WithholdingIncomeRow? row) => new
    {
        label,
        indent,
        heading = formRow is null,
        specify,
        fill = specify && row is not null ? row.Description ?? row.LabelTh : null,
        hasAmount = row is not null,
        date = row is not null ? TaxDocumentRenderModel.Date(row.DatePaid) : null,
        paid = row is not null ? Split(row.AmountPaidSatang) : null,
        withheld = row is not null ? Split(row.TaxWithheldSatang) : null,
    };

    private static object Condition(string number, string label, bool on, string? text) => new { number, label, on, text = on ? text : null };

    private static string? Amount(long? satang) => satang is { } v ? TaxDocumentRenderModel.Money(v) : null;

    // The certificate splits each amount into a baht column and a satang column.
    private static object Split(long satang) => new
    {
        baht = (satang / 100).ToString("N0", CultureInfo.InvariantCulture),
        satang = (satang % 100).ToString("00", CultureInfo.InvariantCulture),
    };
}
