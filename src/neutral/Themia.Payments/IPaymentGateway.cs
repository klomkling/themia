namespace Themia.Payments;

/// <summary>Collects a payment and reports its outcome.</summary>
/// <remarks>
/// Every charge created here is the platform's <b>own revenue</b>. Collecting money on behalf of someone
/// else and settling it later is a licensed payment business in Thailand, so that flow is out of scope by
/// law rather than by preference — see the design document's §1.
/// </remarks>
public interface IPaymentGateway
{
    /// <summary>Creates a charge.</summary>
    /// <param name="request">What to collect, and how it may be paid.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The provider's handle, the status, and what the shopper must do next.</returns>
    /// <exception cref="PaymentApiException">The provider rejected the request or could not be reached.</exception>
    Task<ChargeCreation> CreateChargeAsync(CreateChargeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reads a charge's current state.</summary>
    /// <param name="charge">Which charge.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The charge.</returns>
    /// <exception cref="PaymentApiException">Unknown to the provider, or the provider could not be reached.</exception>
    Task<Charge> GetChargeAsync(ChargeRef charge, CancellationToken cancellationToken = default);

    /// <summary>Refunds a charge, fully or in part.</summary>
    /// <param name="request">Which charge, and how much.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The refund's id where one exists, and its status.</returns>
    /// <exception cref="PaymentApiException">Refused by the provider, or not supported for this charge.</exception>
    Task<RefundCreation> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What an adapter can actually do, so configuration can be checked at startup.</summary>
public interface IPaymentGatewayCapabilities
{
    /// <summary>The methods this adapter can charge with.</summary>
    IReadOnlyList<PaymentMethod> SupportedMethods { get; }
}
