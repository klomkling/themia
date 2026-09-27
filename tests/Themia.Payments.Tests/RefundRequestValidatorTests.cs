using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class RefundRequestValidatorTests
{
    [Theory]
    [MemberData(nameof(CreateChargeRequestValidatorTests.InvalidIdempotencyKeys), MemberType = typeof(CreateChargeRequestValidatorTests))]
    public void An_invalid_idempotency_key_is_refused(string key)
    {
        var ex = Assert.Throws<PaymentApiException>(() => RefundRequestValidator.Validate(
            new RefundRequest(new ChargeRef("ch_1", "order-1"), null, null, key)));

        Assert.Equal(FailureKind.Validation, ex.Kind);
        Assert.Equal("idempotency_key_invalid", ex.ProviderCode);
    }

    [Fact]
    public void A_refund_naming_no_charge_is_an_argument_exception()
    {
        Assert.Throws<ArgumentException>(() => RefundRequestValidator.Validate(
            new RefundRequest(new ChargeRef(null, " "), null, null, null)));
    }

    [Fact]
    public void A_valid_refund_passes()
    {
        RefundRequestValidator.Validate(new RefundRequest(new ChargeRef(null, "order-1"), null, null, "refund-1"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "  ")]
    public void A_charge_ref_with_neither_id_is_an_argument_exception(string? providerChargeId, string referenceId)
    {
        var ex = Assert.Throws<ArgumentException>(() => ChargeRefValidator.Validate(new ChargeRef(providerChargeId, referenceId)));

        Assert.Equal("charge", ex.ParamName);
    }
}
