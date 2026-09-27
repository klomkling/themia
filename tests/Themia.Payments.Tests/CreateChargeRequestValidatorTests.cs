using Themia.Payments;
using Xunit;

namespace Themia.Payments.Tests;

public class CreateChargeRequestValidatorTests
{
    private static CreateChargeRequest Valid() => new()
    {
        Amount = Money.Thb(10000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.QrPromptPay],
    };

    [Fact]
    public void A_zero_amount_is_refused_before_any_call()
    {
        var request = Valid() with { Amount = Money.Thb(0) };

        var ex = Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request));

        Assert.Equal(FailureKind.Validation, ex.Kind);
        Assert.Equal("amount_not_positive", ex.ProviderCode);
        Assert.Equal(0, ex.HttpStatus);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_reference_id_is_refused(string reference)
    {
        var request = Valid() with { ReferenceId = reference };

        Assert.Equal("reference_id_required",
            Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request)).ProviderCode);
    }

    [Fact]
    public void An_empty_method_list_is_refused()
    {
        var request = Valid() with { AllowedMethods = [] };

        Assert.Equal("no_payment_method",
            Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request)).ProviderCode);
    }

    [Fact]
    public void A_duplicated_method_is_refused_rather_than_silently_collapsed()
    {
        var request = Valid() with { AllowedMethods = [PaymentMethod.Card, PaymentMethod.Card] };

        Assert.Equal("duplicate_payment_method",
            Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request)).ProviderCode);
    }

    [Fact]
    public void An_expiry_in_the_past_is_refused()
    {
        var request = Valid() with { ExpiresAt = DateTimeOffset.UnixEpoch };

        Assert.Equal("expiry_in_the_past",
            Assert.Throws<PaymentApiException>(() => CreateChargeRequestValidator.Validate(request)).ProviderCode);
    }

    [Fact]
    public void A_valid_request_passes()
    {
        CreateChargeRequestValidator.Validate(Valid());
    }

    public static TheoryData<string> InvalidIdempotencyKeys => new()
    {
        "",
        "   ",
        new string('k', 256),
        "order-1\nX-Injected: 1",
        "order-1\u0000",
        "คำสั่ง-1",          // non-ASCII: refused by HttpClient at send time, which would look transient
        "order 1",          // keys are restricted to visible ASCII, no spaces
    };

    [Theory]
    [MemberData(nameof(InvalidIdempotencyKeys))]
    public void An_invalid_idempotency_key_is_refused(string key)
    {
        var ex = Assert.Throws<PaymentApiException>(
            () => CreateChargeRequestValidator.Validate(Valid() with { IdempotencyKey = key }));

        Assert.Equal(FailureKind.Validation, ex.Kind);
        Assert.Equal("idempotency_key_invalid", ex.ProviderCode);
        Assert.Equal(0, ex.HttpStatus);
    }

    [Fact]
    public void A_255_character_idempotency_key_is_accepted()
    {
        CreateChargeRequestValidator.Validate(Valid() with { IdempotencyKey = new string('k', 255) });
    }
}
