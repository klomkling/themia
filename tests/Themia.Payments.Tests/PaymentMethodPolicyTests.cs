using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class PaymentMethodPolicyTests
{
    private static PaymentMethodPolicy Policy() => new()
    {
        Currency = "THB",
        Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay, PaymentMethod.MobileBanking])],
        Above = [PaymentMethod.QrPromptPay, PaymentMethod.MobileBanking, PaymentMethod.Card],
    };

    [Fact]
    public void Below_the_boundary_only_the_cheap_methods_are_allowed()
    {
        Assert.Equal(
            [PaymentMethod.QrPromptPay, PaymentMethod.MobileBanking],
            Policy().Resolve(Money.Thb(2500)));
    }

    [Fact]
    public void The_boundary_itself_is_inclusive()
    {
        Assert.DoesNotContain(PaymentMethod.Card, Policy().Resolve(Money.Thb(100000)));
    }

    [Fact]
    public void One_satang_above_the_boundary_opens_the_expensive_methods()
    {
        Assert.Contains(PaymentMethod.Card, Policy().Resolve(Money.Thb(100001)));
    }

    [Fact]
    public void A_currency_the_policy_was_not_written_for_throws()
    {
        var ex = Assert.Throws<PaymentApiException>(() => Policy().Resolve(Money.From(2500, "USD")));

        Assert.Equal("policy_currency_mismatch", ex.ProviderCode);
    }

    [Fact]
    public void Bands_must_ascend()
    {
        var policy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands =
            [
                new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay]),
                new PaymentMethodBand(50000, [PaymentMethod.Card]),
            ],
            Above = [PaymentMethod.Card],
        };

        Assert.Contains("ascending", string.Join(" ", policy.Validate()), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_band_or_empty_above_is_a_configuration_error()
    {
        var policy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [])],
            Above = [],
        };

        Assert.Equal(2, policy.Validate().Count);
    }
}
