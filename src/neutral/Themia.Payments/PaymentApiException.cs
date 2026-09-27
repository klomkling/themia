namespace Themia.Payments;

/// <summary>A provider rejected a request, or could not be reached.</summary>
/// <remarks>
/// A <i>failed payment</i> is not a failed call: a declined charge comes back as
/// <see cref="PaymentStatus.Failed"/> with a <see cref="PaymentFailure"/>, because the request itself was
/// processed correctly. This exception is for the request going wrong.
/// </remarks>
public sealed class PaymentApiException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="kind">The class of failure.</param>
    /// <param name="providerCode">The provider's error code, or an adapter-defined code for a local rejection.</param>
    /// <param name="httpStatus">The HTTP status, or 0 when the failure was local.</param>
    /// <param name="message">A message safe to log; never include a request or response body.</param>
    /// <param name="innerException">The transport failure, when there was one.</param>
    public PaymentApiException(
        FailureKind kind,
        string providerCode,
        int httpStatus,
        string? message = null,
        Exception? innerException = null)
        : base(message ?? $"{kind}: {providerCode} (HTTP {httpStatus}).", innerException)
    {
        Kind = kind;
        ProviderCode = providerCode;
        HttpStatus = httpStatus;
    }

    /// <summary>The class of failure.</summary>
    public FailureKind Kind { get; }

    /// <summary>The provider's error code.</summary>
    public string ProviderCode { get; }

    /// <summary>The HTTP status, or 0 for a local rejection.</summary>
    public int HttpStatus { get; }
}
