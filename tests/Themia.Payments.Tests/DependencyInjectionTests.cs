using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Payments;
using Themia.Payments.DependencyInjection;
using Xunit;

namespace Themia.Payments.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddThemiaPayments_registers_the_gate_and_the_options()
    {
        var services = new ServiceCollection();

        services.AddThemiaPayments(o => o.MethodPolicy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.QrPromptPay, PaymentMethod.Card],
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<PaymentMethodGate>());
        Assert.Equal("THB", provider.GetRequiredService<IOptions<ThemiaPaymentsOptions>>().Value.MethodPolicy!.Currency);
    }

    [Fact]
    public void Registering_twice_does_not_throw_and_adds_the_validator_and_the_gate_once()
    {
        // The adapter's Add… calls AddThemiaPayments itself, and the host usually calls it again with its
        // own policy. Both calls must be safe, must not stack two validators or gates, and the host's
        // later configure delegate must still apply.
        var services = new ServiceCollection();

        services.AddThemiaPayments(o => o.MethodPolicy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.QrPromptPay],
        });
        services.AddThemiaPayments(o => o.MethodPolicy = new PaymentMethodPolicy
        {
            Currency = "USD",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.QrPromptPay],
        });

        Assert.Single(services, d => d.ServiceType == typeof(IValidateOptions<ThemiaPaymentsOptions>));
        Assert.Single(services, d => d.ServiceType == typeof(PaymentMethodGate));
        using var provider = services.BuildServiceProvider();
        Assert.Equal("USD", provider.GetRequiredService<IOptions<ThemiaPaymentsOptions>>().Value.MethodPolicy!.Currency);
    }

    [Fact]
    public void Capabilities_registered_after_the_core_are_still_checked()
    {
        var services = new ServiceCollection();
        services.AddThemiaPayments(o => o.MethodPolicy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.Wallet],
        });
        services.AddSingleton<IPaymentGatewayCapabilities>(new QrOnlyCapabilities());   // after, on purpose

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(
            () => { _ = provider.GetRequiredService<IOptions<ThemiaPaymentsOptions>>().Value; });
    }

    [Fact]
    public void A_policy_naming_a_method_the_adapter_cannot_serve_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPaymentGatewayCapabilities>(new QrOnlyCapabilities());
        services.AddThemiaPayments(o => o.MethodPolicy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.Wallet],
        });

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => { _ = provider.GetRequiredService<IOptions<ThemiaPaymentsOptions>>().Value; });
        Assert.Contains("Wallet", string.Join(" ", ex.Failures), StringComparison.Ordinal);
    }

    private sealed class QrOnlyCapabilities : IPaymentGatewayCapabilities
    {
        public IReadOnlyList<PaymentMethod> SupportedMethods => [PaymentMethod.QrPromptPay];
    }
}
