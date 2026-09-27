namespace Themia.Payments;

/// <summary>The one rule for a caller-supplied idempotency key, shared by the charge and refund validators.</summary>
internal static class IdempotencyKeyRule
{
    /// <summary>The longest key accepted — the common ceiling across providers' idempotency headers and fields.</summary>
    public const int MaxLength = 255;

    /// <summary>
    /// Throws when <paramref name="key"/> is set but blank, longer than <see cref="MaxLength"/>, or contains a
    /// control character (a key travels as an HTTP header, where a CR/LF would split it). Null means "generate one".
    /// </summary>
    /// <exception cref="PaymentApiException"><see cref="FailureKind.Validation"/>, <c>idempotency_key_invalid</c>.</exception>
    public static void Validate(string? key)
    {
        if (key is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxLength || key.Any(char.IsControl))
        {
            throw new PaymentApiException(FailureKind.Validation, "idempotency_key_invalid", httpStatus: 0,
                $"An idempotency key must be non-blank, at most {MaxLength} characters, with no control characters.");
        }
    }
}
