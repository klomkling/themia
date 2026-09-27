namespace Themia.Payments;

/// <summary>The one rule for a caller-supplied idempotency key, shared by the charge and refund validators.</summary>
internal static class IdempotencyKeyRule
{
    /// <summary>The longest key accepted — the common ceiling across providers' idempotency headers and fields.</summary>
    public const int MaxLength = 255;

    /// <summary>
    /// Throws when <paramref name="key"/> is set but blank, longer than <see cref="MaxLength"/>, or contains
    /// anything but visible ASCII (0x21-0x7E). A key travels as an HTTP header: a CR/LF would split it, and a
    /// non-ASCII character is refused by HttpClient at send time, which would read as a transient outage and be
    /// retried. Null means "generate one".
    /// </summary>
    /// <exception cref="PaymentApiException"><see cref="FailureKind.Validation"/>, <c>idempotency_key_invalid</c>.</exception>
    public static void Validate(string? key)
    {
        if (key is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > MaxLength || !key.All(IsVisibleAscii))
        {
            throw new PaymentApiException(FailureKind.Validation, "idempotency_key_invalid", httpStatus: 0,
                $"An idempotency key must be non-blank, at most {MaxLength} characters, of visible ASCII only.");
        }
    }

    private static bool IsVisibleAscii(char c) => c is >= '!' and <= '~';
}
