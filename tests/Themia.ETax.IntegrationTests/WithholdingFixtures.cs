using Themia.ETax.Withholding;

namespace Themia.ETax.IntegrationTests;

internal static class WithholdingFixtures
{
    /// <summary>A certificate that fills every line of the income table, with long Thai and Latin Extended text, to prove it still fits one page.</summary>
    public static WithholdingCertificateSnapshot Full(string number = "WHT-2569-000123")
    {
        var rows = Enum.GetValues<WithholdingFormRow>().Select((row, i) => new WithholdingIncomeRow
        {
            IncomeTypeCode = $"fixture-{i + 1}",
            Section = "40(2)",
            LabelTh = $"รายการที่ {i + 1}: ค่านายหน้า Café Łódź",
            FormRow = row,
            Description = row is WithholdingFormRow.DividendCreditOtherRate or WithholdingFormRow.DividendNoCreditOther or WithholdingFormRow.Section3Tredecim or WithholdingFormRow.Other
                ? $"ค่าจ้างทำของ ระยะเวลาหนึ่งเดือน — Café Łódź {i + 1}"
                : null,
            DatePaid = new DateOnly(2026, 10, 1 + i),
            AmountPaidSatang = 1_234_567_00 + i,
            RateBasisPoints = 300,
            TaxWithheldSatang = WithholdingMath.Withhold(1_234_567_00 + i, 300),
        }).ToArray();

        return new WithholdingCertificateSnapshot
        {
            DocumentNumber = number,
            BookNumber = "12",
            IssueDate = new DateOnly(2026, 10, 15),
            Payer = new TaxParty { Name = "บริษัท ตัวอย่าง จำกัด", TaxId = "0105536078673", Branch = "00000", Address = "1 ถนนสุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110" },
            PayerKind = WithholdingPartyKind.Company,
            PayerLegacyTaxId = "0105536078",
            Payee = new TaxParty { Name = "นายสมชาย ใจดี", TaxId = "1101700230708", Address = "99 หมู่ 1 ตำบลตัวอย่าง อำเภอเมือง จังหวัดเชียงใหม่ 50000" },
            PayeeKind = WithholdingPartyKind.Individual,
            FilingForm = WithholdingFilingForm.Pnd3,
            FilingSequence = 17,
            IncomeCatalogueVersion = "fixture-1",
            Rows = rows,
            Totals = new WithholdingTotals { PaidSatang = rows.Sum(r => r.AmountPaidSatang), WithheldSatang = rows.Sum(r => r.TaxWithheldSatang) },
            Funds = new WithholdingFunds { GovernmentPensionSatang = 150_000, SocialSecuritySatang = 75_000, ProvidentFundSatang = 300_000 },
            Condition = WithholdingCondition.Other,
            ConditionOther = "ออกให้ตามสัญญา",
        };
    }

    /// <summary>The common case: one income line.</summary>
    public static WithholdingCertificateSnapshot Single(string number = "WHT-2569-000001")
    {
        var row = Full().Rows.First(r => r.FormRow == WithholdingFormRow.Fees40_2);
        return Full(number) with
        {
            Rows = [row],
            Totals = new WithholdingTotals { PaidSatang = row.AmountPaidSatang, WithheldSatang = row.TaxWithheldSatang },
            Funds = null,
            FilingSequence = null,
            BookNumber = null,
            PayerLegacyTaxId = null,
            Condition = WithholdingCondition.WithheldAtSource,
            ConditionOther = null,
        };
    }
}
