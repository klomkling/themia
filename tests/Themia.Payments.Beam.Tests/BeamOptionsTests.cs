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

    private static ServiceProvider Provider(Action<BeamOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddThemiaPaymentsBeam(o =>
        {
            o.MerchantId = "m";
            o.ApiKey = "k";
            configure(o);
        });
        return services.BuildServiceProvider();
    }

    private static string[] Failures(Action<BeamOptions> configure)
    {
        using var provider = Provider(configure);
        var ex = Assert.Throws<OptionsValidationException>(
            () => { _ = provider.GetRequiredService<IOptions<BeamOptions>>().Value; });
        return [.. ex.Failures];
    }

    [Theory]
    [InlineData("http://localhost:8099")]
    [InlineData("http://127.0.0.1:8099")]
    [InlineData("http://127.0.0.2:8099")]
    [InlineData("http://[::1]:8099")]
    [InlineData("https://fake-beam.example.test")]
    public void A_playground_override_is_used_instead_of_the_environment_address(string address)
    {
        using var provider = Provider(o => { o.Environment = BeamEnvironment.Playground; o.BaseAddress = new Uri(address); });

        var options = provider.GetRequiredService<IOptions<BeamOptions>>().Value;
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(BeamPaymentGateway.HttpClientName);

        Assert.Equal(new Uri(address), options.BaseAddress);
        Assert.Equal(new Uri(address), client.BaseAddress);
    }

    [Fact]
    public void Without_an_override_the_environment_address_is_used()
    {
        using var provider = Provider(o => o.Environment = BeamEnvironment.Production);

        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(BeamPaymentGateway.HttpClientName);

        Assert.Equal(BeamOptions.BaseAddressFor(BeamEnvironment.Production), client.BaseAddress);
    }

    [Theory]
    [InlineData("http://localhost:8099")]
    [InlineData("https://fake-beam.example.test")]
    public void A_production_override_is_refused_at_startup(string address)
    {
        var failures = string.Join(" ", Failures(o => { o.Environment = BeamEnvironment.Production; o.BaseAddress = new Uri(address); }));

        Assert.Contains("BaseAddress", failures, StringComparison.Ordinal);
        Assert.Contains("Production", failures, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://fake-beam.example.test")]
    [InlineData("http://10.0.0.5:8099")]
    [InlineData("http://localhost.evil.test")]
    public void A_plain_http_override_is_refused_unless_the_host_is_loopback(string address)
    {
        var failures = string.Join(" ", Failures(o => { o.Environment = BeamEnvironment.Playground; o.BaseAddress = new Uri(address); }));

        Assert.Contains("BaseAddress", failures, StringComparison.Ordinal);
        Assert.Contains("loopback", failures, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://user:pass@fake-beam.example.test")]
    [InlineData("https://fake-beam.example.test/?x=1")]
    [InlineData("https://fake-beam.example.test/#frag")]
    [InlineData("https://fake-beam.example.test/v1")]
    [InlineData("ftp://fake-beam.example.test")]
    public void An_override_with_credentials_query_path_or_another_scheme_is_refused(string address)
    {
        var failures = Failures(o => { o.Environment = BeamEnvironment.Playground; o.BaseAddress = new Uri(address); });

        Assert.Contains(failures, f => f.Contains("BaseAddress", StringComparison.Ordinal));
    }

    [Fact]
    public void A_relative_override_is_refused()
    {
        var failures = Failures(o => o.BaseAddress = new Uri("/beam", UriKind.Relative));

        Assert.Contains(failures, f => f.Contains("BaseAddress", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_override_does_not_echo_the_address_credentials()
    {
        var failures = string.Join(" ", Failures(o => o.BaseAddress = new Uri("https://user:s3cret@fake-beam.example.test")));

        Assert.DoesNotContain("s3cret", failures, StringComparison.Ordinal);
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
