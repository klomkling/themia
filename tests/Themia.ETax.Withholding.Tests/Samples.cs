namespace Themia.ETax.Withholding.Tests;

// FIXTURES ONLY. The entries below are invented to exercise the catalogue structure; they are not tax content.
// Real catalogue entries wait for the accountant's answers to coord #0164 Q1/Q2.
internal static class Samples
{
    public const string ValidPayerTaxId = "0105536078673";
    public const string ValidPayeeTaxId = "1101700230708";

    public static IncomeTypeCatalogue Catalogue() => new(
        "test-1",
        [
            new IncomeTypeEntry
            {
                Code = "fixture-fee",
                Section = "40(2)",
                SubItem = null,
                LabelTh = "ค่าธรรมเนียม ค่านายหน้า",
                FormRow = WithholdingFormRow.Fees40_2,
                AllowedFilingForms = [WithholdingFilingForm.Pnd3, WithholdingFilingForm.Pnd53],
                AllowedPayeeKinds = [WithholdingPartyKind.Individual, WithholdingPartyKind.Company],
            },
            new IncomeTypeEntry
            {
                Code = "fixture-royalty",
                Section = "40(3)",
                SubItem = null,
                LabelTh = "ค่าแห่งลิขสิทธิ์",
                FormRow = WithholdingFormRow.Royalty40_3,
                AllowedFilingForms = [WithholdingFilingForm.Pnd3, WithholdingFilingForm.Pnd53],
                AllowedPayeeKinds = [WithholdingPartyKind.Individual, WithholdingPartyKind.Company],
            },
            new IncomeTypeEntry
            {
                Code = "fixture-other",
                Section = "40(8)",
                SubItem = "ก",
                LabelTh = "อื่นๆ",
                FormRow = WithholdingFormRow.Other,
                AllowedFilingForms = [WithholdingFilingForm.Pnd53],
                AllowedPayeeKinds = [WithholdingPartyKind.Company],
            },
        ]);

    public static WithholdingCertificateSnapshot Valid() => new()
    {
        DocumentNumber = "WHT-2569-000123",
        IssueDate = new DateOnly(2026, 10, 15),
        Payer = new TaxParty { Name = "บริษัท ตัวอย่าง จำกัด", TaxId = ValidPayerTaxId, Branch = "00000", Address = "1 ถนนสุขุมวิท กรุงเทพฯ" },
        Payee = new TaxParty { Name = "สมชาย ใจดี", TaxId = ValidPayeeTaxId, Address = "2 ถนนพระราม 4 กรุงเทพฯ" },
        PayerKind = WithholdingPartyKind.Company,
        PayeeKind = WithholdingPartyKind.Individual,
        FilingForm = WithholdingFilingForm.Pnd3,
        IncomeCatalogueVersion = "test-1",
        Rows =
        [
            new WithholdingIncomeRow
            {
                IncomeTypeCode = "fixture-fee",
                Section = "40(2)",
                LabelTh = "ค่าธรรมเนียม ค่านายหน้า",
                FormRow = WithholdingFormRow.Fees40_2,
                DatePaid = new DateOnly(2026, 10, 14),
                AmountPaidSatang = 1_000_000,
                RateBasisPoints = 300,
                TaxWithheldSatang = 30_000,
            },
        ],
        Totals = new WithholdingTotals { PaidSatang = 1_000_000, WithheldSatang = 30_000 },
        Condition = WithholdingCondition.WithheldAtSource,
    };
}
