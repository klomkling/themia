namespace Themia.Payments;

/// <summary>Rejects a refund request the adapters should never send.</summary>
/// <remarks>Local rejections carry <see cref="FailureKind.Validation"/> and an HTTP status of 0, as on a charge.</remarks>
public static class RefundRequestValidator
{
    /// <summary>Validates a request.</summary>
    /// <param name="request">The request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="RefundRequest.Charge"/> names no charge.</exception>
    /// <exception cref="PaymentApiException">The idempotency key is blank, over 255 characters, or not visible ASCII.</exception>
    public static void Validate(RefundRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ChargeRefValidator.Validate(request.Charge);
        IdempotencyKeyRule.Validate(request.IdempotencyKey);
    }
}
