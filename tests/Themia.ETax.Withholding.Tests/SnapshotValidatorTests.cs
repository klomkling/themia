using Xunit;

namespace Themia.ETax.Withholding.Tests;

public class SnapshotValidatorTests
{
    private static readonly IncomeTypeCatalogue Catalogue = Samples.Catalogue();

    private static IReadOnlyList<WithholdingValidationError> Validate(WithholdingCertificateSnapshot s, WithholdingValidationOptions? o = null) =>
        WithholdingSnapshotValidator.Validate(s, Catalogue, o);

    private static WithholdingIncomeRow Row(WithholdingCertificateSnapshot s) => s.Rows[0];

    private static WithholdingCertificateSnapshot WithRow(WithholdingIncomeRow row, long? paid = null, long? withheld = null) =>
        Samples.Valid() with
        {
            Rows = [row],
            Totals = new WithholdingTotals { PaidSatang = paid ?? row.AmountPaidSatang, WithheldSatang = withheld ?? row.TaxWithheldSatang },
        };

    private static void AssertSingle(IReadOnlyList<WithholdingValidationError> errors, WithholdingValidationCode code, string field) =>
        Assert.Equal([new WithholdingValidationError(code, field)], errors);

    [Fact]
    public void Validate_ShouldAccept_AValidSnapshot() => Assert.Empty(Validate(Samples.Valid()));

    [Fact]
    public void Validate_ShouldThrow_OnNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => WithholdingSnapshotValidator.Validate(null!, Catalogue));
        Assert.Throws<ArgumentNullException>(() => WithholdingSnapshotValidator.Validate(Samples.Valid(), null!));
    }

    [Fact]
    public void Validate_ShouldRequire_TheHeader()
    {
        var errors = Validate(Samples.Valid() with { DocumentNumber = " ", IssueDate = default, SchemaVersion = 9 });

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "document_number"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "issue_date"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "schema_version"), errors);
    }

    [Fact]
    public void Validate_ShouldRequire_PayerTaxIdBranchAndAddress()
    {
        var errors = Validate(Samples.Valid() with { Payer = new TaxParty { Name = "x" } });

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "payer.tax_id"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "payer.branch"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "payer.address"), errors);
    }

    [Fact]
    public void Validate_ShouldRequire_PayeeNameTaxIdAndAddress_ButNotBranch()
    {
        var errors = Validate(Samples.Valid() with { Payee = new TaxParty { Name = " " } });

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "payee.name"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "payee.tax_id"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "payee.address"), errors);
        Assert.DoesNotContain(errors, e => e.Field == "payee.branch");
    }

    [Fact]
    public void Validate_ShouldRejectMalformedTaxIdsAndBranches()
    {
        var s = Samples.Valid();
        var errors = Validate(s with
        {
            Payer = s.Payer with { TaxId = "123", Branch = "1" },
            Payee = s.Payee with { TaxId = "abcdefghijklm", Branch = "x" },
        });

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "payer.tax_id"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "payer.branch"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "payee.tax_id"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "payee.branch"), errors);
    }

    [Fact]
    public void Validate_ShouldCheckTheTaxIdChecksum_UnlessSwitchedOff()
    {
        var s = Samples.Valid() with { Payee = Samples.Valid().Payee with { TaxId = "1101700230700" } };

        AssertSingle(Validate(s), WithholdingValidationCode.InvalidChecksum, "payee.tax_id");
        Assert.Empty(Validate(s, new WithholdingValidationOptions { ValidateTaxIdChecksum = false }));
    }

    [Fact]
    public void Validate_ShouldRejectUndefinedEnums()
    {
        var errors = Validate(Samples.Valid() with
        {
            PayeeKind = (WithholdingPayeeKind)99,
            FilingForm = (WithholdingFilingForm)99,
            Condition = (WithholdingCondition)99,
        });

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "payee_kind"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "filing_form"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "condition"), errors);
    }

    [Fact]
    public void Validate_ShouldRefuse_ACatalogueVersionThatIsNotTheOneInUse() =>
        AssertSingle(Validate(Samples.Valid() with { IncomeCatalogueVersion = "test-0" }), WithholdingValidationCode.CatalogueMismatch, "income_catalogue_version");

    [Fact]
    public void Validate_ShouldRefuse_AnIncomeTypeCodeTheCatalogueDoesNotHave()
    {
        var s = Samples.Valid();
        AssertSingle(Validate(WithRow(Row(s) with { IncomeTypeCode = "nope" })), WithholdingValidationCode.CatalogueMismatch, "rows[0].income_type_code");
    }

    [Theory]
    [InlineData("section")]
    [InlineData("sub_item")]
    [InlineData("label_th")]
    [InlineData("form_row")]
    public void Validate_ShouldRefuse_ARowWhosePrintedCopyDiffersFromTheCatalogue(string field)
    {
        var row = Row(Samples.Valid());
        row = field switch
        {
            "section" => row with { Section = "40(1)" },
            "sub_item" => row with { SubItem = "ข" },
            "label_th" => row with { LabelTh = "อย่างอื่น" },
            _ => row with { FormRow = 5 },
        };

        AssertSingle(Validate(WithRow(row)), WithholdingValidationCode.CatalogueMismatch, $"rows[0].{field}");
    }

    [Fact]
    public void Validate_ShouldRefuse_AFilingFormOrPayeeKindTheIncomeTypeDoesNotAllow()
    {
        var s = Samples.Valid();
        var other = new WithholdingIncomeRow
        {
            IncomeTypeCode = "fixture-other",
            Section = "40(8)",
            SubItem = "ก",
            LabelTh = "อื่นๆ",
            FormRow = 6,
            DatePaid = Row(s).DatePaid,
            AmountPaidSatang = 1_000_000,
            RateBasisPoints = 300,
            TaxWithheldSatang = 30_000,
        };
        var errors = Validate(WithRow(other));

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.NotAllowed, "filing_form"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.NotAllowed, "payee_kind"), errors);
        Assert.Empty(Validate(WithRow(other) with { FilingForm = WithholdingFilingForm.Pnd53, PayeeKind = WithholdingPayeeKind.Juristic }));
    }

    [Fact]
    public void Validate_ShouldRequireAtLeastOneRow() =>
        Assert.Contains(
            new WithholdingValidationError(WithholdingValidationCode.Required, "rows"),
            Validate(Samples.Valid() with { Rows = [] }));

    [Fact]
    public void Validate_ShouldRejectADatePaidThatIsMissingOrAfterTheIssueDate()
    {
        var row = Row(Samples.Valid());

        AssertSingle(Validate(WithRow(row with { DatePaid = default })), WithholdingValidationCode.Required, "rows[0].date_paid");
        AssertSingle(Validate(WithRow(row with { DatePaid = new DateOnly(2026, 10, 16) })), WithholdingValidationCode.Malformed, "rows[0].date_paid");
        Assert.Empty(Validate(WithRow(row with { DatePaid = new DateOnly(2026, 10, 15) })));
    }

    [Fact]
    public void Validate_ShouldRejectNegativeAmounts()
    {
        var row = Row(Samples.Valid()) with { AmountPaidSatang = -1, TaxWithheldSatang = -1, RateBasisPoints = null };

        var errors = Validate(WithRow(row, 0, 0));

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "rows[0].amount_paid_satang"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "rows[0].tax_withheld_satang"), errors);
    }

    [Fact]
    public void Validate_ShouldRejectARateOutsideZeroToTenThousandBasisPoints()
    {
        var row = Row(Samples.Valid()) with { RateBasisPoints = 10_001 };

        Assert.Contains(
            new WithholdingValidationError(WithholdingValidationCode.Malformed, "rows[0].rate_basis_points"),
            Validate(WithRow(row)));
    }

    [Fact]
    public void Validate_ShouldRecomputeTheWithheldAmount_FromTheRate()
    {
        var row = Row(Samples.Valid()) with { TaxWithheldSatang = 30_001 };

        AssertSingle(Validate(WithRow(row)), WithholdingValidationCode.AmountsInconsistent, "rows[0].tax_withheld_satang");
    }

    [Fact]
    public void Validate_ShouldAcceptAnyWithheldAmountUpToThePaid_WhenThereIsNoRate()
    {
        var row = Row(Samples.Valid()) with { RateBasisPoints = null, TaxWithheldSatang = 12_345 };

        Assert.Empty(Validate(WithRow(row)));
    }

    [Fact]
    public void Validate_ShouldRefuse_WithheldAbovePaid()
    {
        var row = Row(Samples.Valid()) with { RateBasisPoints = null, TaxWithheldSatang = 1_000_001 };

        AssertSingle(Validate(WithRow(row)), WithholdingValidationCode.WithheldExceedsPaid, "rows[0].tax_withheld_satang");
    }

    [Fact]
    public void Validate_ShouldRefuse_TotalsThatAreNotTheSumOfTheRows()
    {
        var errors = Validate(Samples.Valid() with { Totals = new WithholdingTotals { PaidSatang = 1, WithheldSatang = 2 } });

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.AmountsInconsistent, "totals.paid_satang"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.AmountsInconsistent, "totals.withheld_satang"), errors);
    }

    [Fact]
    public void Validate_ShouldSumSeveralRows()
    {
        var s = Samples.Valid();
        var second = Row(s) with { AmountPaidSatang = 50, TaxWithheldSatang = 2 }; // 1.5 satang rounds to 2

        Assert.Empty(Validate(s with
        {
            Rows = [Row(s), second],
            Totals = new WithholdingTotals { PaidSatang = 1_000_050, WithheldSatang = 30_002 },
        }));
    }

    [Fact]
    public void Validate_ShouldReportOverflowingAmountsAsMalformed_NotThrow()
    {
        var s = Samples.Valid();
        var big = Row(s) with { AmountPaidSatang = long.MaxValue, RateBasisPoints = null, TaxWithheldSatang = 0 };

        var errors = Validate(s with
        {
            Rows = [big, big],
            Totals = new WithholdingTotals { PaidSatang = long.MaxValue, WithheldSatang = 0 },
        });

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "totals"), errors);
    }

    [Fact]
    public void Validate_ShouldRequireTheOtherConditionText_OnlyForOther()
    {
        AssertSingle(Validate(Samples.Valid() with { Condition = WithholdingCondition.Other }), WithholdingValidationCode.Required, "condition_other");
        AssertSingle(Validate(Samples.Valid() with { ConditionOther = "x" }), WithholdingValidationCode.NotAllowed, "condition_other");
        Assert.Empty(Validate(Samples.Valid() with { Condition = WithholdingCondition.Other, ConditionOther = "อื่นๆ" }));
    }

    [Fact]
    public void Validate_ShouldRejectNegativeFundsFilingSequenceAndBlankBookNumber()
    {
        var errors = Validate(Samples.Valid() with
        {
            Funds = new WithholdingFunds { ProvidentFundSatang = -1 },
            FilingSequence = 0,
            BookNumber = " ",
        });

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "funds.provident_fund_satang"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "filing_sequence"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Malformed, "book_number"), errors);
    }

    [Fact]
    public void Validate_ShouldRequire_TheReplacementFields()
    {
        var errors = Validate(Samples.Valid() with
        {
            Replacement = new TaxDocumentReplacement
            {
                ReplacesDocumentId = Guid.Empty,
                OriginalDocumentNumber = " ",
                OriginalIssueDate = default,
                Reason = "",
            },
        });

        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "replacement.replaces_document_id"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "replacement.original_document_number"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "replacement.original_issue_date"), errors);
        Assert.Contains(new WithholdingValidationError(WithholdingValidationCode.Required, "replacement.reason"), errors);
    }

    [Fact]
    public void Validate_ShouldRefuse_CharactersTheEmbeddedFontsCannotPrint_WithoutEchoingThem()
    {
        var s = Samples.Valid();
        var errors = Validate(s with { Payee = s.Payee with { Name = "山田太郎" } });

        AssertSingle(errors, WithholdingValidationCode.UnsupportedCharacter, "payee.name");
        Assert.DoesNotContain("山", errors[0].ToString(), StringComparison.Ordinal);
    }
}
