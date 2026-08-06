using Xunit;

namespace Themia.PromptPay.Tests;

/// <summary>
/// Byte-for-byte payloads published by an independent PromptPay implementation, reproduced here.
/// </summary>
/// <remarks>
/// <b>These are confirmed vectors. Do not edit an expected string to make a test pass.</b> Each one was
/// checked before any of this package existed, by recomputing its checksum with a bitwise CRC written
/// from the algorithm rather than from anyone's lookup table. They are the only evidence in this suite
/// that the encoding is right rather than merely self-consistent — a test suite that compares this
/// package's output to this package's output proves nothing about what a bank app will accept.
/// <para>
/// If one of these fails, the change under test altered the wire format. That is either a defect or a
/// deliberate break, and either way the answer is not to update the constant.
/// </para>
/// </remarks>
public class GoldenVectorTests
{
    [Fact]
    public void CreditTransfer_MobileNumber_NoAmount()
    {
        Assert.Equal(
            "00020101021129370016A0000006770101110113006681222333353037645802TH63041DCF",
            PromptPayQr.CreditTransfer(PromptPayProxy.MobileNumber("0812223333")));
    }

    [Fact]
    public void CreditTransfer_MobileNumber_WithAmount()
    {
        Assert.Equal(
            "00020101021229370016A0000006770101110113006681222333353037645802TH540530.0063043CAD",
            PromptPayQr.CreditTransfer(PromptPayProxy.MobileNumber("0812223333"), 30m));
    }

    [Fact]
    public void CreditTransfer_MobileNumber_AnotherNumberAndAmount()
    {
        Assert.Equal(
            "00020101021229370016A0000006770101110113006680111111153037645802TH540520.15630442BE",
            PromptPayQr.CreditTransfer(PromptPayProxy.MobileNumber("0801111111"), 20.15m));
    }

    [Fact]
    public void BillPayment_NoAmount()
    {
        var biller = BillerRegistration.PerProductSuffix("0999999999999", "90");

        Assert.Equal(
            "00020101021130550016A0000006770101120115099999999999990021211122233344453037645802TH63043EE7",
            PromptPayQr.BillPayment(biller, "111222333444"));
    }

    [Fact]
    public void BillPayment_WithReference2AndAmount()
    {
        var biller = BillerRegistration.PerProductSuffix("0994000165501", "00");

        Assert.Equal(
            "00020101021230650016A00000067701011201150994000165501000212123456789012030667042953037645802TH54073649.2263044534",
            PromptPayQr.BillPayment(biller, "123456789012", 3649.22m, "670429"));
    }

    [Fact]
    public void A_thirteen_character_biller_id_cannot_be_expressed_and_that_is_the_guard()
    {
        // One published vector uses the 13-character biller id "0112233445566" — a registration with no
        // suffix. Such biller ids exist, and this package deliberately cannot emit one: requiring the
        // Tax ID and the 2-digit suffix separately is the whole point of BillerRegistration, because a
        // suffix that can be omitted is a suffix two products can silently share.
        //
        // Recorded as a test rather than a comment so the constraint is visible when someone hits it and
        // reaches for "just let the caller pass a biller id".
        Assert.Throws<ArgumentException>(() => BillerRegistration.PerProductSuffix("0112233445566", string.Empty));
        Assert.Throws<ArgumentException>(() => BillerRegistration.PerProductSuffix("01122334455", "66"));
    }
}
