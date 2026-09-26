using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Themia.Payments.Beam.Internal;

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
    /// <exception cref="PaymentApiException">
    /// The request was refused locally, the policy left no allowed method, or Beam rejected the call.
    /// </exception>
    public async Task<ChargeCreation> CreateChargeAsync(CreateChargeRequest request, CancellationToken cancellationToken = default)
    {
        CreateChargeRequestValidator.Validate(request);
        var allowed = gate.Apply(request.Amount, request.AllowedMethods);
        var beamOptions = options.Value;
        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        // Exactly one method, and Beam names a charge type for it, goes straight to a charge. Everything
        // else — two or more methods, or MobileBanking/Wallet alone, which have no direct charge type —
        // goes through a payment link instead.
        if (allowed.Count == 1 && BeamMapping.ToDirectChargeType(allowed[0]) is { } chargeType)
        {
            return await CreateDirectChargeAsync(httpClient, beamOptions, request, chargeType, cancellationToken)
                .ConfigureAwait(false);
        }

        return await BeamPaymentLinks.CreateAsync(httpClient, beamOptions, request, allowed, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="PaymentApiException">Beam rejected the call, or no charge could be found.</exception>
    /// <remarks>
    /// Resolves in three steps: a provider charge id reads <c>GET /api/v1/charges/{id}</c> directly; a 404 there
    /// is retried as a payment link id (<c>GET /api/v1/payment-links/{id}</c>), following a paid link to the
    /// charge that paid it; with no provider id at all, the app's reference id is looked up instead.
    /// </remarks>
    public async Task<Charge> GetChargeAsync(ChargeRef charge, CancellationToken cancellationToken = default)
    {
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var (resolved, _) = await BeamCharges.ResolveAsync(httpClient, options.Value, charge, cancellationToken)
            .ConfigureAwait(false);
        return resolved;
    }

    /// <inheritdoc />
    /// <exception cref="PaymentApiException">
    /// No charge could be resolved for the reference id, a payment link id names a link no charge has paid, the charge is not a <c>CARD</c> charge but a partial
    /// amount was requested, a partial amount's currency does not match the charge's, or Beam rejected the call.
    /// </exception>
    /// <remarks>
    /// The charge is resolved first exactly as <see cref="GetChargeAsync"/> resolves it, so a payment link id
    /// refunds the charge that paid the link.
    /// Beam refunds only <c>CARD</c> charges in part — a QR PromptPay charge refunds in full or not at all —
    /// so a partial <paramref name="request"/> reads the charge first to check the payment method before posting.
    /// </remarks>
    public async Task<RefundCreation> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var beamOptions = options.Value;
        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        // The same resolution GetChargeAsync uses: a payment link id (what a link-based ChargeCreation carries)
        // must become the id of the charge that paid it, because Beam refunds charges, not links.
        var (resolved, isUnpaidLink) = await BeamCharges.ResolveAsync(httpClient, beamOptions, request.Charge, cancellationToken)
            .ConfigureAwait(false);
        if (isUnpaidLink)
        {
            throw new PaymentApiException(FailureKind.NotFound, "no_charge_for_link", 0);
        }

        var chargeId = resolved.ChargeId;

        if (request.Amount is { } amount)
        {
            var (charge, paymentMethodType) = await BeamCharges.GetByIdWithPaymentMethodAsync(
                httpClient, beamOptions, chargeId, cancellationToken).ConfigureAwait(false);

            if (paymentMethodType != "CARD")
            {
                throw new PaymentApiException(FailureKind.Validation, "partial_refund_unsupported", 0,
                    $"Beam refunds only CARD charges in part; this charge was paid by {paymentMethodType ?? "an unknown method"}. Refund it in full or not at all.");
            }

            if (!string.Equals(amount.Currency, charge.Amount.Currency, StringComparison.Ordinal))
            {
                throw new PaymentApiException(FailureKind.Validation, "refund_currency_mismatch", 0);
            }
        }

        return await CreateRefundAsync(httpClient, beamOptions, chargeId, request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RefundCreation> CreateRefundAsync(
        HttpClient httpClient, BeamOptions beamOptions, string chargeId, RefundRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            new RefundRequestBody { ChargeId = chargeId, Reason = request.Reason, Amount = request.Amount?.MinorUnits },
            BeamMapping.SerializeOptions);
        var idempotencyKey = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");
        using var response = await BeamHttp.SendWithRetryAsync(
            httpClient,
            HttpMethod.Post,
            "/api/v1/refunds",
            beamOptions,
            () => new StringContent(payload, Encoding.UTF8, "application/json"),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var (parsed, root) = await BeamMapping.TryParseAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw BeamMapping.ToApiException(response.StatusCode, parsed ? root : null);
            }

            if (!parsed || !BeamMapping.TryGetNonEmptyString(root, "refundId", out var refundId))
            {
                throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
            }

            return new RefundCreation(refundId, PaymentStatus.Pending);
        }
    }

    private static async Task<ChargeCreation> CreateDirectChargeAsync(
        HttpClient httpClient, BeamOptions beamOptions, CreateChargeRequest request, string chargeType,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(BuildChargeRequestBody(request, chargeType), BeamMapping.SerializeOptions);
        var idempotencyKey = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");
        using var response = await BeamHttp.SendWithRetryAsync(
            httpClient,
            HttpMethod.Post,
            "/api/v1/charges",
            beamOptions,
            () => new StringContent(payload, Encoding.UTF8, "application/json"),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var (parsed, root) = await BeamMapping.TryParseAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw BeamMapping.ToApiException(response.StatusCode, parsed ? root : null);
            }

            var chargeId = root.GetProperty("chargeId").GetString()!;
            return new ChargeCreation(chargeId, PaymentStatus.Pending, BeamMapping.ToNextAction(root, (int)response.StatusCode));
        }
    }

    private static ChargeRequestBody BuildChargeRequestBody(CreateChargeRequest request, string chargeType) =>
        new()
        {
            Amount = request.Amount.MinorUnits,
            Currency = request.Amount.Currency,
            ReferenceId = request.ReferenceId,
            PaymentMethod = new ChargeMethodBody
            {
                PaymentMethodType = chargeType,
                QrPromptPay = chargeType == "QR_PROMPT_PAY" && request.ExpiresAt is { } expiry
                    ? new QrPromptPaySettings { ExpiryTime = expiry }
                    : null,
            },
            ReturnUrl = request.ReturnUrl,
        };

    private sealed class ChargeRequestBody
    {
        [JsonPropertyName("amount")]
        public required long Amount { get; init; }

        [JsonPropertyName("currency")]
        public required string Currency { get; init; }

        [JsonPropertyName("referenceId")]
        public required string ReferenceId { get; init; }

        [JsonPropertyName("paymentMethod")]
        public required ChargeMethodBody PaymentMethod { get; init; }

        [JsonPropertyName("returnUrl")]
        public Uri? ReturnUrl { get; init; }
    }

    private sealed class ChargeMethodBody
    {
        [JsonPropertyName("paymentMethodType")]
        public required string PaymentMethodType { get; init; }

        [JsonPropertyName("qrPromptPay")]
        public QrPromptPaySettings? QrPromptPay { get; init; }
    }

    private sealed class QrPromptPaySettings
    {
        [JsonPropertyName("expiryTime")]
        public required DateTimeOffset ExpiryTime { get; init; }
    }

    private sealed class RefundRequestBody
    {
        [JsonPropertyName("chargeId")]
        public required string ChargeId { get; init; }

        [JsonPropertyName("reason")]
        public string? Reason { get; init; }

        [JsonPropertyName("amount")]
        public long? Amount { get; init; }
    }
}
