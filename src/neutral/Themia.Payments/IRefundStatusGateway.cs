namespace Themia.Payments;

/// <summary>
/// Reads the refunds of a charge back from the provider. Separate from <see cref="IPaymentGateway"/> because only
/// some providers can answer, and because a provider with no refund webhook (2C2P) leaves this as the only way to
/// learn that a pending refund settled.
/// </summary>
public interface IRefundStatusGateway
{
    /// <summary>Lists the refunds of a charge, oldest first as the provider returns them.</summary>
    /// <param name="charge">Which charge.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The refunds, empty when there are none.</returns>
    /// <exception cref="PaymentApiException">Refused or unknown to the provider, not supported, or the provider could not be reached.
    /// Reading is safe to repeat.</exception>
    Task<IReadOnlyList<RefundRecord>> GetRefundsAsync(ChargeRef charge, CancellationToken cancellationToken = default);
}
