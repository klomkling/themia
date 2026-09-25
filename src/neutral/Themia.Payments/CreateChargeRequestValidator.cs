namespace Themia.Payments;

/// <summary>Rejects a charge request the adapters should never send.</summary>
/// <remarks>
/// Local rejections carry <see cref="FailureKind.Validation"/> and an HTTP status of 0, so a caller can tell
/// "we refused this" from "the provider refused this" without parsing a message.
/// </remarks>
public static class CreateChargeRequestValidator
{
    /// <summary>Validates a request.</summary>
    /// <param name="request">The request.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="PaymentApiException">The request is not one any provider should be sent.</exception>
    public static void Validate(CreateChargeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Amount.MinorUnits <= 0)
        {
            throw Refuse("amount_not_positive", "A charge amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(request.ReferenceId))
        {
            throw Refuse("reference_id_required", "A charge needs the caller's own reference id.");
        }

        if (request.AllowedMethods.Count == 0)
        {
            throw Refuse("no_payment_method", "A charge needs at least one allowed payment method.");
        }

        if (request.AllowedMethods.Distinct().Count() != request.AllowedMethods.Count)
        {
            throw Refuse("duplicate_payment_method", "AllowedMethods contains the same method twice.");
        }

        if (request.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
        {
            throw Refuse("expiry_in_the_past", "ExpiresAt is already past.");
        }
    }

    private static PaymentApiException Refuse(string code, string message) =>
        new(FailureKind.Validation, code, httpStatus: 0, message);
}
