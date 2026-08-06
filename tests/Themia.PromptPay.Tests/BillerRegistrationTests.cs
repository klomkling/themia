using Xunit;

namespace Themia.PromptPay.Tests;

/// <summary>
/// The product-discriminator guard (coord #0055, #0052 item 2). An earlier design took the biller id and
/// suffix as separate required inputs and called that the fix; it protects only the branch where the bank
/// issues one suffix per product. These pin both branches.
/// </summary>
public class BillerRegistrationTests
{
    private const string TaxId = "0105500000000";

    [Fact]
    public void PerProductSuffix_puts_the_discriminator_in_the_biller_id()
    {
        var ezyAssets = BillerRegistration.PerProductSuffix(TaxId, "01");
        var propertiezy = BillerRegistration.PerProductSuffix(TaxId, "02");

        Assert.Equal("010550000000001", ezyAssets.BillerId);
        Assert.Equal("010550000000002", propertiezy.BillerId);
        Assert.Null(ezyAssets.ProductPrefix);

        // Same reference, different products: the payloads must differ, or the two are indistinguishable
        // at the receiving account.
        Assert.NotEqual(
            PromptPayQr.BillPayment(ezyAssets, "INV-00042"),
            PromptPayQr.BillPayment(propertiezy, "INV-00042"));
    }

    [Fact]
    public void SharedSuffix_puts_the_discriminator_in_the_reference_without_the_caller_doing_it()
    {
        // The branch the original guard missed. Both products hold the same suffix, so the biller id is
        // identical and the reference is the only thing left. The caller passes its own running number
        // and nothing else; the prefix is not theirs to remember.
        var ezyAssets = BillerRegistration.SharedSuffix(TaxId, "01", "EA-");
        var propertiezy = BillerRegistration.SharedSuffix(TaxId, "01", "PZ-");

        Assert.Equal(ezyAssets.BillerId, propertiezy.BillerId);
        Assert.NotEqual(
            PromptPayQr.BillPayment(ezyAssets, "00042"),
            PromptPayQr.BillPayment(propertiezy, "00042"));

        Assert.Contains("EA-00042", PromptPayQr.BillPayment(ezyAssets, "00042"), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedSuffix_cannot_be_constructed_without_naming_the_product()
    {
        Assert.Throws<ArgumentException>(() => BillerRegistration.SharedSuffix(TaxId, "01", string.Empty));
        Assert.Throws<ArgumentException>(() => BillerRegistration.SharedSuffix(TaxId, "01", "   "));
        Assert.Throws<ArgumentNullException>(() => BillerRegistration.SharedSuffix(TaxId, "01", null!));
    }

    [Fact]
    public void MaxReferenceLength_is_derived_from_the_format_not_guessed()
    {
        // Tag 30's value is capped at 99 characters and always spends 20 on the AID and 19 on a 15-digit
        // biller id, leaving 60 for the references, of which Reference 1's own header takes 4.
        Assert.Equal(56, BillerRegistration.PerProductSuffix(TaxId, "01").MaxReferenceLength);

        // A product prefix comes out of the same budget — the point ezy-assets raised on #0055.
        Assert.Equal(53, BillerRegistration.SharedSuffix(TaxId, "01", "EA-").MaxReferenceLength);
    }

    [Fact]
    public void An_over_long_reference_is_refused_rather_than_emitted()
    {
        var biller = BillerRegistration.PerProductSuffix(TaxId, "01");

        Assert.NotNull(PromptPayQr.BillPayment(biller, new string('9', 56)));

        var tooLong = Assert.Throws<ArgumentException>(() => PromptPayQr.BillPayment(biller, new string('9', 57)));
        Assert.Contains("57", tooLong.Message, StringComparison.Ordinal);
        Assert.Contains("56", tooLong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reference2_takes_its_share_of_the_same_limit()
    {
        // The headline number is the no-Reference-2 case. A payload that fit without one must stop
        // fitting with one, or the limit is being applied to the wrong budget.
        var biller = BillerRegistration.PerProductSuffix(TaxId, "01");
        var reference = new string('9', 56);

        Assert.NotNull(PromptPayQr.BillPayment(biller, reference));
        Assert.Throws<ArgumentException>(() => PromptPayQr.BillPayment(biller, reference, reference2: "670429"));

        // 56 - (4 + 6) = 46 still fits alongside a 6-character Reference 2.
        Assert.NotNull(PromptPayQr.BillPayment(biller, new string('9', 46), reference2: "670429"));
    }

    [Fact]
    public void The_prefix_is_counted_when_the_reference_is_length_checked()
    {
        // The failure ezy-assets predicted: a reference that fits on its own and does not once the prefix
        // is applied. Themia owns the concatenation precisely so this is checked rather than discovered
        // when a bank rejects the payload.
        var shared = BillerRegistration.SharedSuffix(TaxId, "01", "EA-");

        Assert.NotNull(PromptPayQr.BillPayment(shared, new string('9', 53)));
        Assert.Throws<ArgumentException>(() => PromptPayQr.BillPayment(shared, new string('9', 54)));
    }

    [Fact]
    public void A_configured_limit_may_only_tighten_the_structural_one()
    {
        var tightened = BillerRegistration.PerProductSuffix(TaxId, "01", maxReferenceLength: 20);
        Assert.Equal(20, tightened.MaxReferenceLength);
        Assert.Throws<ArgumentException>(() => PromptPayQr.BillPayment(tightened, new string('9', 21)));

        // Widening past what the format allows would emit a payload no reader can parse.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BillerRegistration.PerProductSuffix(TaxId, "01", maxReferenceLength: 57));
    }

    [Theory]
    [InlineData("12345678901234")]   // 14 digits
    [InlineData("012345678901")]     // 12 digits
    [InlineData("01055000000O0")]    // letter O
    public void A_tax_id_that_is_not_thirteen_digits_is_refused(string taxId)
    {
        Assert.Throws<ArgumentException>(() => BillerRegistration.PerProductSuffix(taxId, "01"));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("001")]
    [InlineData("A1")]
    public void A_suffix_that_is_not_two_digits_is_refused(string suffix)
    {
        Assert.Throws<ArgumentException>(() => BillerRegistration.PerProductSuffix(TaxId, suffix));
    }
}
