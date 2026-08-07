using Xunit;

namespace Themia.PromptPay.Tests;

/// <summary>Encoding rules that the golden vectors happen to cover only for one shape each.</summary>
public class PayloadTests
{
    private static readonly BillerRegistration Biller = BillerRegistration.PerProductSuffix("0105500000000", "01");

    [Theory]
    [InlineData("0812223333")]
    [InlineData("081-222-3333")]
    [InlineData("081 222 3333")]
    [InlineData("+66812223333")]
    [InlineData("66812223333")]
    public void Formatting_is_stripped_and_every_thai_form_reaches_the_same_payload(string entered)
    {
        Assert.Equal(
            PromptPayQr.CreditTransfer(PromptPayProxy.MobileNumber("0812223333")),
            PromptPayQr.CreditTransfer(PromptPayProxy.MobileNumber(entered)));
    }

    [Theory]
    [InlineData("4155552671")]        // a US number in national form: 10 digits, no leading 0
    [InlineData("081222333")]         // 9 digits
    [InlineData("08122233334")]       // 11 digits, not starting 66
    [InlineData("081222333X")]
    public void A_number_that_is_not_recognisably_thai_is_refused_rather_than_reinterpreted(string entered)
    {
        // Reinterpreting another country's national format as Thai does not fail — it succeeds, at
        // whoever holds the resulting Thai number.
        Assert.Throws<ArgumentException>(() => PromptPayProxy.MobileNumber(entered));
    }

    [Fact]
    public void The_point_of_initiation_says_whether_the_amount_is_fixed()
    {
        // "11" is a reusable QR the payer types an amount into; "12" is one-time with the amount baked
        // in. Getting this backwards produces a payload that scans and then asks for the wrong thing.
        Assert.StartsWith("000201010211", PromptPayQr.BillPayment(Biller, "INV-1"), StringComparison.Ordinal);
        Assert.StartsWith("000201010212", PromptPayQr.BillPayment(Biller, "INV-1", 100m), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, "540530.00")]
    [InlineData(20.15, "540520.15")]
    [InlineData(3649.22, "54073649.22")]
    [InlineData(0.5, "54040.50")]
    [InlineData(1000000, "54101000000.00")]
    public void An_amount_is_always_two_decimals(decimal amount, string expectedTag)
    {
        Assert.Contains(expectedTag, PromptPayQr.BillPayment(Biller, "INV-1", amount), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_amount_is_refused(decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PromptPayQr.BillPayment(Biller, "INV-1", amount));
    }

    [Fact]
    public void The_checksum_covers_its_own_tag_header()
    {
        // The most common way to get EMVCo wrong: computing the CRC over the payload without the "6304"
        // that introduces it. Recomputed here independently of the production path.
        var payload = PromptPayQr.BillPayment(Biller, "INV-00042", 590m);
        var body = payload[..^4];
        var declared = payload[^4..];

        Assert.EndsWith("6304" + declared, payload, StringComparison.Ordinal);
        Assert.Equal(declared, Crc(body));

        // And the same checksum over the body WITHOUT "6304" must not match, or the assertion above would
        // pass for an implementation that omitted it.
        Assert.NotEqual(declared, Crc(body[..^4]));
    }

    [Fact]
    public void A_non_ascii_reference_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PromptPayQr.BillPayment(Biller, "ใบแจ้งหนี้-1"));
    }

    // A second, independent CRC-16/CCITT-FALSE. Deliberately not the production one: a test that calls
    // the code under test to compute its own expectation asserts only that the method is deterministic.
    private static string Crc(string input)
    {
        var crc = 0xFFFF;
        foreach (var character in input)
        {
            crc ^= character << 8;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x1021) & 0xFFFF : (crc << 1) & 0xFFFF;
            }
        }

        return crc.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
    }
}
