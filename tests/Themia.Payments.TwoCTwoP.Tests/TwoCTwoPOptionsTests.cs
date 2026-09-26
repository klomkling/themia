using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Themia.Payments.TwoCTwoP.DependencyInjection;

using Xunit;

namespace Themia.Payments.TwoCTwoP.Tests;

public class TwoCTwoPOptionsTests
{
    [Fact]
    public void Sandbox_and_production_have_different_base_addresses()
    {
        Assert.Equal(new Uri("https://sandbox-pgw.2c2p.com"), TwoCTwoPOptions.BaseAddressFor(TwoCTwoPEnvironment.Sandbox));
        Assert.Equal(new Uri("https://pgw.2c2p.com"), TwoCTwoPOptions.BaseAddressFor(TwoCTwoPEnvironment.Production));
    }

    [Fact]
    public void A_missing_merchant_id_fails_validation_at_startup()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsTwoCTwoP(o =>
        {
            o.MerchantId = "";
            o.SecretKey = "secret";
            o.Environment = TwoCTwoPEnvironment.Sandbox;
        });

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => { _ = provider.GetRequiredService<IOptions<TwoCTwoPOptions>>().Value; });
        Assert.Contains("MerchantId", string.Join(" ", ex.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_secret_key_fails_validation_at_startup()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsTwoCTwoP(o =>
        {
            o.MerchantId = "m";
            o.SecretKey = "";
            o.Environment = TwoCTwoPEnvironment.Sandbox;
        });

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => { _ = provider.GetRequiredService<IOptions<TwoCTwoPOptions>>().Value; });
        Assert.Contains("SecretKey", string.Join(" ", ex.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void The_gateway_resolves_to_the_2c2p_implementation()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsTwoCTwoP(o =>
        {
            o.MerchantId = "m";
            o.SecretKey = "secret";
            o.Environment = TwoCTwoPEnvironment.Sandbox;
        });

        using var provider = services.BuildServiceProvider();

        Assert.IsType<TwoCTwoPPaymentGateway>(provider.GetRequiredService<IPaymentGateway>());
        Assert.IsType<TwoCTwoPPaymentGateway>(provider.GetRequiredService<IPaymentGatewayCapabilities>());
    }

    [Fact]
    public void The_webhook_verifier_resolves_to_the_2c2p_implementation()
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsTwoCTwoP(o =>
        {
            o.MerchantId = "m";
            o.SecretKey = "secret";
            o.Environment = TwoCTwoPEnvironment.Sandbox;
        });

        using var provider = services.BuildServiceProvider();

        Assert.IsType<TwoCTwoPWebhookVerifier>(provider.GetRequiredService<IPaymentWebhookVerifier>());
    }
}
