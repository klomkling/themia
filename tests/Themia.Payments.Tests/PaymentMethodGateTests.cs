using Microsoft.Extensions.Options;
using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class PaymentMethodGateTests
{
    private static PaymentMethodGate Gate(PaymentMethodPolicy? policy) =>
        new(Options.Create(new ThemiaPaymentsOptions { MethodPolicy = policy }));

    private static PaymentMethodPolicy SmallTicketPolicy() => new()
    {
        Currency = "THB",
        Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
        Above = [PaymentMethod.QrPromptPay, PaymentMethod.Card],
    };

    [Fact]
    public void With_no_policy_the_callers_list_passes_through_untouched()
    {
        var requested = new[] { PaymentMethod.Card, PaymentMethod.QrPromptPay };

        Assert.Equal(requested, Gate(policy: null).Apply(Money.Thb(500), requested));
    }

    [Fact]
    public void The_result_is_the_intersection_in_the_callers_order()
    {
        var allowed = Gate(SmallTicketPolicy())
            .Apply(Money.Thb(500), [PaymentMethod.Card, PaymentMethod.QrPromptPay]);

        Assert.Equal([PaymentMethod.QrPromptPay], allowed);
    }

    [Fact]
    public void An_empty_intersection_throws_and_names_both_lists()
    {
        var ex = Assert.Throws<PaymentApiException>(() =>
            Gate(SmallTicketPolicy()).Apply(Money.Thb(500), [PaymentMethod.Card]));

        Assert.Equal("method_not_allowed_for_amount", ex.ProviderCode);
        Assert.Contains("Card", ex.Message, StringComparison.Ordinal);
        Assert.Contains("QrPromptPay", ex.Message, StringComparison.Ordinal);
    }
}
