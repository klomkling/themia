using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class MoneyTests
{
    [Fact]
    public void Thb_carries_minor_units_and_currency()
    {
        var money = Money.Thb(10000);

        Assert.Equal(10000, money.MinorUnits);
        Assert.Equal("THB", money.Currency);
    }

    [Fact]
    public void Currency_is_upper_cased_so_comparison_can_stay_ordinal()
    {
        Assert.Equal("USD", Money.From(500, "usd").Currency);
    }

    [Theory]
    [InlineData("TH")]
    [InlineData("THBB")]
    [InlineData("TH1")]
    [InlineData("")]
    public void A_currency_that_is_not_three_letters_is_refused(string currency)
    {
        Assert.ThrowsAny<ArgumentException>(() => Money.From(100, currency));
    }

    [Fact]
    public void A_negative_amount_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Thb(-1));
    }

    [Fact]
    public void Zero_is_allowed_because_a_refund_of_zero_means_the_maximum_refundable_amount()
    {
        Assert.Equal(0, Money.Thb(0).MinorUnits);
    }
}
