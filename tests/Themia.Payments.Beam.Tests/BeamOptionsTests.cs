using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Payments;
using Themia.Payments.Beam;
using Themia.Payments.Beam.DependencyInjection;
using Xunit;

namespace Themia.Payments.Beam.Tests;

public class BeamOptionsTests
{
    [Fact]
    public void Playground_and_production_have_different_base_addresses()
    {
        Assert.Equal(new Uri("https://playground.api.beamcheckout.com"), BeamOptions.BaseAddressFor(BeamEnvironment.Playground));
        Assert.Equal(new Uri("https://api.beamcheckout.com"), BeamOptions.BaseAddressFor(BeamEnvironment.Production));
    }

    [Fact]
    public void A_missing_merchant_id_fails_validation_at_startup()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsBeam(o =>
        {
            o.MerchantId = "";
            o.ApiKey = "key";
            o.Environment = BeamEnvironment.Playground;
        });

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => { _ = provider.GetRequiredService<IOptions<BeamOptions>>().Value; });
        Assert.Contains("MerchantId", string.Join(" ", ex.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void The_adapter_declares_what_it_can_charge_with()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsBeam(o =>
        {
            o.MerchantId = "m";
            o.ApiKey = "k";
            o.Environment = BeamEnvironment.Playground;
        });

        using var provider = services.BuildServiceProvider();

        // QrPromptPay and Card are charged directly; MobileBanking and Wallet go through a payment link
        // (Task 7), so all four are genuinely servable.
        Assert.Equal(
            [PaymentMethod.QrPromptPay, PaymentMethod.Card, PaymentMethod.MobileBanking, PaymentMethod.Wallet],
            provider.GetRequiredService<IPaymentGatewayCapabilities>().SupportedMethods);
    }
}
