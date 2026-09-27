using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class PaymentContractsTests
{
    [Fact]
    public void A_charge_reference_always_carries_the_apps_own_id()
    {
        var reference = new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1");

        Assert.Equal("order-1", reference.ReferenceId);
        Assert.Null(reference.ProviderChargeId);
    }

    [Fact]
    public void Next_action_is_a_closed_hierarchy_so_a_switch_breaks_when_a_case_is_added()
    {
        NextAction action = new NextAction.ShowQr([1, 2, 3], "00020101", null);

        var described = action switch
        {
            NextAction.None => "none",
            NextAction.Redirect r => r.Url.ToString(),
            NextAction.ShowQr qr => $"qr:{qr.ImagePng.Length}",
            _ => throw new InvalidOperationException("unreachable"),
        };

        Assert.Equal("qr:3", described);
    }

    [Fact]
    public void An_api_exception_carries_the_provider_code_and_status()
    {
        var ex = new PaymentApiException(FailureKind.RateLimited, "TOO_MANY_REQUESTS_ERROR", 429);

        Assert.Equal(FailureKind.RateLimited, ex.Kind);
        Assert.Equal("TOO_MANY_REQUESTS_ERROR", ex.ProviderCode);
        Assert.Equal(429, ex.HttpStatus);
    }
}
