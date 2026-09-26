using Themia.Payments.Beam.Tests;

using Xunit;

namespace Themia.Payments.ContractTests;

/// <summary>
/// One suite, run against every adapter (<see cref="BeamContractTests"/>, <see cref="TwoCTwoPContractTests"/>).
/// A change that fits only one provider fails here — this is what keeps the core from drifting back into a
/// single provider's shape.
/// </summary>
public abstract class PaymentGatewayContract
{
    /// <summary>Builds the adapter under test, wired to <paramref name="handler"/> and, when given, restricted
    /// by <paramref name="policy"/>.</summary>
    protected abstract IPaymentGateway CreateGateway(StubHandler handler, PaymentMethodPolicy? policy = null);

    /// <summary>Scripts <paramref name="handler"/> with this provider's response to creating a charge.</summary>
    protected abstract void ScriptChargeCreated(StubHandler handler);

    /// <summary>Scripts <paramref name="handler"/> so that reading back <c>ChargeRef("ch_1", "order-1")</c>
    /// returns a succeeded charge for <see cref="Request"/>'s amount.</summary>
    protected abstract void ScriptChargeSucceeded(StubHandler handler);

    [Fact]
    public async Task Creating_a_charge_returns_a_pending_charge_with_a_next_action()
    {
        var handler = new StubHandler();
        ScriptChargeCreated(handler);

        var creation = await CreateGateway(handler).CreateChargeAsync(Request());

        Assert.Equal(PaymentStatus.Pending, creation.Status);
        Assert.NotNull(creation.ChargeId);
        Assert.IsNotType<NextAction.None>(creation.Action);
    }

    [Fact]
    public async Task A_zero_amount_never_reaches_the_provider()
    {
        var handler = new StubHandler();

        await Assert.ThrowsAsync<PaymentApiException>(
            () => CreateGateway(handler).CreateChargeAsync(Request() with { Amount = Money.Thb(0) }));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task The_method_policy_is_honoured_by_every_adapter()
    {
        var handler = new StubHandler();
        var policy = new PaymentMethodPolicy
        {
            Currency = "THB",
            Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
            Above = [PaymentMethod.QrPromptPay, PaymentMethod.Card],
        };

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => CreateGateway(handler, policy)
            .CreateChargeAsync(Request() with { Amount = Money.Thb(50000), AllowedMethods = [PaymentMethod.Card] }));

        Assert.Equal("method_not_allowed_for_amount", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_succeeded_charge_reads_back_with_its_amount_and_no_failure()
    {
        var handler = new StubHandler();
        ScriptChargeSucceeded(handler);

        var charge = await CreateGateway(handler).GetChargeAsync(new ChargeRef("ch_1", "order-1"));

        Assert.Equal(PaymentStatus.Succeeded, charge.Status);
        Assert.Null(charge.Failure);
        Assert.Equal(Money.Thb(100000), charge.Amount);
    }

    private static CreateChargeRequest Request() => new()
    {
        Amount = Money.Thb(100000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.QrPromptPay],
    };
}
