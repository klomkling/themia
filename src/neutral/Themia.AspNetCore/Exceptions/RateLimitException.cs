namespace Themia.AspNetCore.Exceptions;

/// <summary>Rate limit / cooldown hit (e.g. OTP resend). Maps to HTTP 429 with a <c>Retry-After</c>
/// header and a <c>retryAfterSeconds</c> problem extension. <paramref name="retryAfterSeconds"/> is a
/// domain value (the cooldown), so the exception stays HTTP-agnostic — the middleware owns the mapping.</summary>
public sealed class RateLimitException(
    string message,
    int retryAfterSeconds,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata)
{
    /// <summary>Seconds the client should wait before retrying (the <c>Retry-After</c> value).</summary>
    public int RetryAfterSeconds { get; } = retryAfterSeconds >= 0
        ? retryAfterSeconds
        : throw new ArgumentOutOfRangeException(nameof(retryAfterSeconds), retryAfterSeconds, "Must be non-negative.");
}
