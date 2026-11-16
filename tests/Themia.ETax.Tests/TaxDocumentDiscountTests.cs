using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Themia.ETax.Tests;

public sealed class TaxDocumentDiscountTests
{
    private const string Label = "Credit applied";
    private const int Rate = 700;

    // 1,000.00 incl. VAT order, 400.00 paid with credit, 7% VAT: purchase 934.58, discount 373.83, remaining 560.75, VAT 39.25, total 600.00.
    private static TaxDocumentSnapshot Discounted(long lines = 93_458, long discount = 37_383, long exVat = 56_075, long vat = 3_925, long incl = 60_000) =>
        Samples.Receipt() with
        {
            Lines = [new TaxDocumentLine { Description = "Boost package", AmountExVatSatang = lines }],
            Discount = new TaxDocumentDiscount { Description = Label, AmountExVatSatang = discount },
            Totals = new TaxDocumentTotals
            {
                ExVatSatang = exVat,
                VatSatang = vat,
                InclVatSatang = incl,
                WithheldSatang = 0,
                NetReceivedSatang = incl,
                VatRateBasisPoints = Rate,
            },
        };

    private static IReadOnlyList<TaxDocumentValidationError> Validate(TaxDocumentSnapshot s) =>
        TaxDocumentSnapshotValidator.Validate(s, new TaxDocumentValidationOptions { ValidateTaxIdChecksum = false });

    private static void AssertSingle(IReadOnlyList<TaxDocumentValidationError> errors, TaxDocumentValidationCode code, string field)
    {
        var error = Assert.Single(errors);
        Assert.Equal(code, error.Code);
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void Validate_ShouldPass_ForADiscountedReceipt()
    {
        Assert.Empty(Validate(Discounted()));
    }

    [Fact]
    public void Validate_ShouldPass_WhenTheDiscountEqualsTheLines_AndEverythingIsZero()
    {
        Assert.Empty(Validate(Discounted(discount: 93_458, exVat: 0, vat: 0, incl: 0)));
    }

    [Fact]
    public void Validate_ShouldPass_ForAZeroDiscount()
    {
        Assert.Empty(Validate(Discounted(discount: 0, exVat: 93_458, vat: 6_542, incl: 100_000)));
    }

    [Fact]
    public void Validate_ShouldRejectANegativeDiscount()
    {
        var errors = Validate(Discounted(discount: -1, exVat: 93_458, vat: 6_542, incl: 100_000));

        AssertSingle(errors, TaxDocumentValidationCode.Malformed, "discount.amount_ex_vat_satang");
    }

    [Fact]
    public void Validate_ShouldRejectADiscountAboveTheLines()
    {
        var errors = Validate(Discounted(discount: 93_459, exVat: 0, vat: 0, incl: 0));

        AssertSingle(errors, TaxDocumentValidationCode.Malformed, "discount.amount_ex_vat_satang");
    }

    [Fact]
    public void Validate_ShouldRejectTotalsThatIgnoreTheDiscount()
    {
        var errors = Validate(Discounted(exVat: 93_458, vat: 6_542, incl: 100_000));

        Assert.Contains(errors, e => e is { Code: TaxDocumentValidationCode.TotalsInconsistent, Field: "totals.ex_vat_satang" });
    }

    [Fact]
    public void Validate_ShouldRejectVatThatIsNotWorkedOutOnTheRemainingAmount()
    {
        var errors = Validate(Discounted(vat: 3_926, incl: 60_001));

        AssertSingle(errors, TaxDocumentValidationCode.TotalsInconsistent, "totals.vat_satang");
    }

    [Theory]
    [InlineData(TaxDocumentKind.CreditNote)]
    [InlineData(TaxDocumentKind.DebitNote)]
    public void Validate_ShouldRefuseADiscount_OnNotes(TaxDocumentKind kind)
    {
        var errors = Validate(Discounted() with { Kind = kind });

        Assert.Contains(errors, e => e is { Code: TaxDocumentValidationCode.NotAllowed, Field: "discount" });
    }

    [Fact]
    public void Validate_ShouldRefuseADiscount_OnAnAbbreviatedReceipt()
    {
        var errors = Validate(Discounted() with { Form = TaxDocumentForm.Abbreviated });

        Assert.Contains(errors, e => e is { Code: TaxDocumentValidationCode.NotAllowed, Field: "discount" });
    }

    [Fact]
    public void Validate_ShouldRequireADescription()
    {
        var s = Discounted() with { Discount = new TaxDocumentDiscount { Description = " ", AmountExVatSatang = 37_383 } };

        AssertSingle(Validate(s), TaxDocumentValidationCode.Required, "discount.description");
    }

    [Fact]
    public void Validate_ShouldRefuseACharacterTheFontCannotPrint()
    {
        var s = Discounted() with { Discount = new TaxDocumentDiscount { Description = "credit \U0001F600", AmountExVatSatang = 37_383 } };

        AssertSingle(Validate(s), TaxDocumentValidationCode.UnsupportedCharacter, "discount.description");
    }

    [Fact]
    public void Validate_ShouldReportOverflow_AsMalformedTotals_NotThrow()
    {
        var s = Discounted() with
        {
            Lines = [new TaxDocumentLine { Description = "a", AmountExVatSatang = long.MaxValue }, new TaxDocumentLine { Description = "b", AmountExVatSatang = 1 }],
        };

        Assert.Contains(Validate(s), e => e.Code == TaxDocumentValidationCode.Malformed);
    }

    // The recipe in TaxDocumentDiscount: net = Split(cash paid), discount = Split(order) - net. 1.01 THB order, 0.50 paid with credit, 0.51 paid in cash.
    [Fact]
    public void Recipe_ShouldKeepTheTotalEqualToTheCashPaid()
    {
        var cash = 51L;
        var net = VatMath.Split(cash, Rate);
        var discount = VatMath.Split(101, Rate).ExVatSatang - net.ExVatSatang;
        var s = Discounted(lines: VatMath.Split(101, Rate).ExVatSatang, discount: discount, exVat: net.ExVatSatang, vat: net.VatSatang, incl: cash);

        Assert.Empty(Validate(s));
        Assert.Equal(cash, s.Totals.InclVatSatang);
    }

    // The naive split of the credit alone passes the validator yet prints 0.50 against 0.51 paid: the reason the recipe is documented.
    [Fact]
    public void NaiveSplitOfTheCredit_ShouldPassTheValidator_YetMissTheCashPaid()
    {
        var order = VatMath.Split(101, Rate).ExVatSatang;
        var discount = VatMath.Split(50, Rate).ExVatSatang;
        var net = VatMath.AddVat(order - discount, Rate);
        var s = Discounted(lines: order, discount: discount, exVat: net.ExVatSatang, vat: net.VatSatang, incl: net.InclVatSatang);

        Assert.Empty(Validate(s));
        Assert.NotEqual(51L, s.Totals.InclVatSatang);
    }

    [Fact]
    public void Serialize_ShouldRoundTripTheDiscount_AndOmitItWhenAbsent()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Discounted());
        var back = TaxDocumentSnapshotSerializer.Deserialize(json, TaxDocumentSnapshotSerializer.ComputeHash(json));

        Assert.Equal(Label, back.Discount?.Description);
        Assert.Equal(37_383, back.Discount?.AmountExVatSatang);
        Assert.DoesNotContain("discount", TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt()));
    }

    public static TheoryData<string> Versions() => new("4", "5", "6", "7");

    public static TheoryData<string> FrozenVersions() => new("1", "2", "3");

    private static TaxDocumentRenderer Renderer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering();
        return services.BuildServiceProvider().GetRequiredService<TaxDocumentRenderer>();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void RenderHtml_ShouldPrintPurchaseLessRemainingVatTotal_InOrder(string version)
    {
        var html = Renderer().RenderHtml(Discounted(), version, TaxDocumentMarker.Original);

        var positions = new[] { "934.58", Label, "373.83", "560.75", "39.25", "600.00" }.Select(t => html.IndexOf(t, StringComparison.Ordinal)).ToArray();
        Assert.True(positions.All(p => p >= 0), string.Join(",", positions));
        Assert.Equal(positions.Order(), positions);
    }

    [Theory]
    [MemberData(nameof(FrozenVersions))]
    public void RenderHtml_ShouldRefuseADiscount_OnTheFrozenSingleSheetVersions(string version)
    {
        Assert.Throws<ArgumentException>(() => Renderer().RenderHtml(Discounted(), version, TaxDocumentMarker.Original));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void RenderHtml_ShouldPrintNoDiscountRows_WhenThereIsNoDiscount(string version)
    {
        var html = Renderer().RenderHtml(Samples.Receipt(), version, TaxDocumentMarker.Original);

        Assert.DoesNotContain("Remaining", html);
        Assert.DoesNotContain("Purchase", html);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void RenderHtml_ShouldPrintZeroRows_WhenCreditCoversTheWholeOrder(string version)
    {
        var html = Renderer().RenderHtml(Discounted(discount: 93_458, exVat: 0, vat: 0, incl: 0), version, TaxDocumentMarker.Original);

        Assert.Contains("934.58", html);
        Assert.Contains("0.00", html);
    }

    [Theory]
    [InlineData("1", false)]
    [InlineData("3", false)]
    [InlineData("4", true)]
    [InlineData("7", true)]
    public async Task Issue_ShouldAcceptADiscount_OnlyUnderThePagedVersions(string version, bool accepted)
    {
        var flow = new Flow(templates: new TaxDocumentTemplateOptions { TemplateVersion = version });

        var result = await flow.IssueAsync(Flow.Request(snapshot: Discounted()));

        Assert.Equal(accepted, result.Outcome == TaxDocumentIssueOutcome.Issued);
        if (!accepted)
            Assert.Contains(result.Errors!, e => e is { Code: TaxDocumentValidationCode.NotAllowed, Field: "discount" });
    }
}
