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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!")]
    [InlineData("AAECAwQFBgc=")] // valid base64 of only 8 bytes
    public void An_unusable_webhook_key_fails_validation_at_startup_without_echoing_it(string key)
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsBeam(o =>
        {
            o.MerchantId = "m";
            o.ApiKey = "k";
            o.WebhookHmacKey = key;
        });

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => { _ = provider.GetRequiredService<IOptions<BeamOptions>>().Value; });
        var failures = string.Join(" ", ex.Failures);
        Assert.Contains("WebhookHmacKey", failures, StringComparison.Ordinal);
        if (key.Trim().Length > 0)
        {
            Assert.DoesNotContain(key, failures, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")] // 16 bytes, the minimum
    public void An_absent_or_sufficiently_long_webhook_key_passes_validation(string? key)
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsBeam(o =>
        {
            o.MerchantId = "m";
            o.ApiKey = "k";
            o.WebhookHmacKey = key;
        });

        using var provider = services.BuildServiceProvider();

        Assert.Equal(key, provider.GetRequiredService<IOptions<BeamOptions>>().Value.WebhookHmacKey);
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

        // A lone QrPromptPay is charged directly; everything else goes through a payment link, so all four
        // are genuinely servable.
        Assert.Equal(
            [PaymentMethod.QrPromptPay, PaymentMethod.Card, PaymentMethod.MobileBanking, PaymentMethod.Wallet],
            provider.GetRequiredService<IPaymentGatewayCapabilities>().SupportedMethods);
    }

    [Fact]
    public void The_webhook_verifier_resolves_to_the_beam_implementation()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsBeam(o =>
        {
            o.MerchantId = "m";
            o.ApiKey = "k";
            o.Environment = BeamEnvironment.Playground;
        });

        using var provider = services.BuildServiceProvider();

        Assert.IsType<BeamWebhookVerifier>(provider.GetRequiredService<IPaymentWebhookVerifier>());
    }

    [Fact]
    public void The_beam_only_client_resolves_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsBeam(o =>
        {
            o.MerchantId = "m";
            o.ApiKey = "k";
            o.Environment = BeamEnvironment.Playground;
        });

        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<BeamPaymentClient>();
        Assert.Same(client, provider.GetRequiredService<BeamPaymentClient>());
    }
}
