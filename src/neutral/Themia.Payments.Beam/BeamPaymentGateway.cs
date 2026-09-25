using Microsoft.Extensions.Options;

namespace Themia.Payments.Beam;

/// <summary>Beam Checkout's <see cref="IPaymentGateway"/> adapter.</summary>
/// <remarks>
/// QR PromptPay and card are charged directly; mobile banking and wallet go through a Beam payment link.
/// All four are genuinely servable, even though the request-building path differs per method — see Task 7.
/// </remarks>
public sealed class BeamPaymentGateway : IPaymentGateway, IPaymentGatewayCapabilities
{
    /// <summary>The name this adapter's <see cref="HttpClient"/> is registered under.</summary>
    public const string HttpClientName = "themia-payments-beam";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<BeamOptions> options;
    private readonly PaymentMethodGate gate;

    /// <summary>Creates the gateway.</summary>
    /// <param name="httpClientFactory">Used to create the named <see cref="HttpClient"/>.</param>
    /// <param name="options">The Beam credentials and environment.</param>
    /// <param name="gate">Applies the shared method policy before building a payload.</param>
    /// <exception cref="ArgumentNullException">Any parameter is null.</exception>
    public BeamPaymentGateway(IHttpClientFactory httpClientFactory, IOptions<BeamOptions> options, PaymentMethodGate gate)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);

        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.gate = gate;
    }

    /// <summary>
    /// The methods this adapter can charge with: <see cref="PaymentMethod.QrPromptPay"/> and
    /// <see cref="PaymentMethod.Card"/> directly, <see cref="PaymentMethod.MobileBanking"/> and
    /// <see cref="PaymentMethod.Wallet"/> through a payment link.
    /// </summary>
    public IReadOnlyList<PaymentMethod> SupportedMethods { get; } =
    [
        PaymentMethod.QrPromptPay,
        PaymentMethod.Card,
        PaymentMethod.MobileBanking,
        PaymentMethod.Wallet,
    ];

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Not yet implemented; see Task 7.</exception>
    public Task<ChargeCreation> CreateChargeAsync(CreateChargeRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Implemented in Task 7/8/9.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Not yet implemented; see Task 8.</exception>
    public Task<Charge> GetChargeAsync(ChargeRef charge, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Implemented in Task 7/8/9.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Not yet implemented; see Task 9.</exception>
    public Task<RefundCreation> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Implemented in Task 7/8/9.");
}
