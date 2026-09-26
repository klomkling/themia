using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Themia.Payments.TwoCTwoP.Internal;

namespace Themia.Payments.TwoCTwoP;

/// <summary>2C2P PGW 4.3 (Redirect API) <see cref="IPaymentGateway"/> adapter.</summary>
/// <remarks>
/// Every request and response body is <c>{"payload": "&lt;JWT&gt;"}</c>, HS256-signed with the merchant secret
/// (spec §7b) — there is no bearer or basic header, and every response is verified before it is trusted (a
/// response that fails verification is never parsed). 2C2P mints no charge id before payment, so
/// <see cref="ChargeCreation.ChargeId"/> and <see cref="Charge.ChargeId"/> are always the caller's own
/// <see cref="CreateChargeRequest.ReferenceId"/> — 2C2P's <c>invoiceNo</c>. Refunds go through the (XML) Payment
/// Process endpoint, added in Task 14; this adapter does not yet support them.
/// </remarks>
public sealed class TwoCTwoPPaymentGateway : IPaymentGateway, IPaymentGatewayCapabilities
{
    /// <summary>The name this adapter's <see cref="HttpClient"/> is registered under.</summary>
    public const string HttpClientName = "themia-payments-2c2p";

    private static readonly IReadOnlyList<PaymentMethod> AllChannels =
    [
        PaymentMethod.Card,
        PaymentMethod.QrPromptPay,
        PaymentMethod.MobileBanking,
        PaymentMethod.Wallet,
    ];

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<TwoCTwoPOptions> options;
    private readonly PaymentMethodGate gate;

    /// <summary>Creates the gateway.</summary>
    /// <param name="httpClientFactory">Used to create the named <see cref="HttpClient"/>.</param>
    /// <param name="options">The 2C2P credentials and environment.</param>
    /// <param name="gate">Applies the shared method policy before building a payload.</param>
    /// <exception cref="ArgumentNullException">Any parameter is null.</exception>
    public TwoCTwoPPaymentGateway(IHttpClientFactory httpClientFactory, IOptions<TwoCTwoPOptions> options, PaymentMethodGate gate)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);

        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.gate = gate;
    }

    /// <summary>
    /// The methods this merchant is enabled for with 2C2P: <see cref="TwoCTwoPOptions.PaymentChannels"/> when
    /// configured, otherwise all four — 2C2P has no capability-discovery call, so an empty configuration is
    /// read as "not restricted" rather than "none".
    /// </summary>
    public IReadOnlyList<PaymentMethod> SupportedMethods =>
        options.Value.PaymentChannels.Count > 0 ? options.Value.PaymentChannels : AllChannels;

    /// <inheritdoc />
    /// <exception cref="PaymentApiException">
    /// The request was refused locally (including a reference id over 20 characters, or a method this merchant
    /// is not enabled for), or 2C2P rejected the call.
    /// </exception>
    public async Task<ChargeCreation> CreateChargeAsync(CreateChargeRequest request, CancellationToken cancellationToken = default)
    {
        CreateChargeRequestValidator.Validate(request);
        if (request.ReferenceId.Length > 20)
        {
            throw Refuse("reference_id_too_long", "2C2P's invoiceNo is capped at 20 characters.");
        }

        var allowed = gate.Apply(request.Amount, request.AllowedMethods);
        var supported = SupportedMethods;
        var unsupported = allowed.Where(method => !supported.Contains(method)).ToArray();
        if (unsupported.Length > 0)
        {
            throw Refuse("method_not_supported",
                $"AllowedMethods contains [{string.Join(", ", unsupported)}], which this 2C2P merchant is not " +
                $"enabled for (it supports [{string.Join(", ", supported)}]).");
        }

        var twoCTwoPOptions = options.Value;
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var idempotencyKey = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");

        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["merchantID"] = twoCTwoPOptions.MerchantId,
            ["invoiceNo"] = request.ReferenceId,
            ["amount"] = TwoCTwoPMapping.ToDecimalAmount(request.Amount),
            ["currencyCode"] = request.Amount.Currency,
            ["paymentChannel"] = allowed.Select(TwoCTwoPMapping.ToChannelCode).ToArray(),
            ["idempotencyID"] = idempotencyKey,
        };
        if (request.Description is { Length: > 0 } description)
        {
            claims["description"] = description;
        }

        if (request.ReturnUrl is { } returnUrl)
        {
            claims["frontendReturnUrl"] = returnUrl.ToString();
        }

        var body = JsonSerializer.Serialize(
            new PayloadEnvelope { Payload = JwtHs256.Encode(claims, twoCTwoPOptions.SecretKey) });

        using var response = await TwoCTwoPHttp.SendWithRetryAsync(
            httpClient, "/payment/4.3/paymentToken",
            () => new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);

        var verified = await ReadVerifiedAsync(response, twoCTwoPOptions.SecretKey, cancellationToken).ConfigureAwait(false);
        if (!TwoCTwoPMapping.TryGetNonEmptyString(verified, "respCode", out var respCode))
        {
            throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
        }

        if (respCode != "0000")
        {
            var respDesc = TwoCTwoPMapping.TryGetNonEmptyString(verified, "respDesc", out var desc) ? desc : null;
            throw new PaymentApiException(
                TwoCTwoPMapping.ToFailureKind((int)response.StatusCode, respCode), respCode, (int)response.StatusCode, respDesc);
        }

        if (!TwoCTwoPMapping.TryGetNonEmptyString(verified, "webPaymentUrl", out var webPaymentUrl))
        {
            throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
        }

        return new ChargeCreation(request.ReferenceId, PaymentStatus.Pending, new NextAction.Redirect(new Uri(webPaymentUrl)));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="charge"/> carries neither a reference id nor a provider charge id.</exception>
    /// <exception cref="PaymentApiException">2C2P found no such transaction, or rejected the call.</exception>
    /// <remarks>2C2P's Payment Inquiry reads by <c>invoiceNo</c>, which is the app's own reference id — the
    /// provider charge id is only a fallback, and only because 2C2P's invoice number and charge id are the same value.</remarks>
    public async Task<Charge> GetChargeAsync(ChargeRef charge, CancellationToken cancellationToken = default)
    {
        var invoiceNo = !string.IsNullOrEmpty(charge.ReferenceId)
            ? charge.ReferenceId
            : charge.ProviderChargeId is { Length: > 0 } providerChargeId
                ? providerChargeId
                : throw new ArgumentException("A ChargeRef needs a ReferenceId or a ProviderChargeId.", nameof(charge));

        var twoCTwoPOptions = options.Value;
        var httpClient = httpClientFactory.CreateClient(HttpClientName);

        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["merchantID"] = twoCTwoPOptions.MerchantId,
            ["invoiceNo"] = invoiceNo,
        };
        var body = JsonSerializer.Serialize(
            new PayloadEnvelope { Payload = JwtHs256.Encode(claims, twoCTwoPOptions.SecretKey) });

        using var response = await TwoCTwoPHttp.SendWithRetryAsync(
            httpClient, "/payment/4.3/paymentInquiry",
            () => new StringContent(body, Encoding.UTF8, "application/json"),
            cancellationToken).ConfigureAwait(false);

        var verified = await ReadVerifiedAsync(response, twoCTwoPOptions.SecretKey, cancellationToken).ConfigureAwait(false);
        if (!TwoCTwoPMapping.TryGetNonEmptyString(verified, "respCode", out var respCode))
        {
            throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
        }

        if (respCode == "2002")
        {
            var respDesc = TwoCTwoPMapping.TryGetNonEmptyString(verified, "respDesc", out var desc) ? desc : null;
            throw new PaymentApiException(FailureKind.NotFound, respCode, (int)response.StatusCode, respDesc);
        }

        return TwoCTwoPMapping.ToCharge(verified, invoiceNo, respCode, (int)response.StatusCode, twoCTwoPOptions.TransactionTimeOffset);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Not supported in this version: refund from the 2C2P merchant portal. 2C2P refunds go through the Payment
    /// Maintenance API, which needs the merchant's RSA key pair and 2C2P's certificate (JWE inside JWS) rather
    /// than the shared secret every other call here uses (spec §7b).
    /// </remarks>
    /// <exception cref="PaymentApiException">Always, with <see cref="FailureKind.Validation"/> and
    /// <c>refund_not_supported</c>. Nothing is sent to 2C2P.</exception>
    public Task<RefundCreation> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default) =>
        Task.FromException<RefundCreation>(new PaymentApiException(FailureKind.Validation, "refund_not_supported", httpStatus: 0,
            "The 2C2P adapter does not support refunds; refund from the 2C2P merchant portal."));

    /// <summary>
    /// Parses the response body and returns its verified <c>payload</c> contents. Throws for anything else: a
    /// body that does not parse, one whose <c>payload</c> JWT fails verification, or one with neither a
    /// verifiable <c>payload</c> nor a plain <c>respCode</c> error shape (ruling: never parse an unverified payload).
    /// </summary>
    private static async Task<JsonElement> ReadVerifiedAsync(
        HttpResponseMessage response, string secretKey, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var (parsed, root) = await TwoCTwoPMapping.TryParseAsync(stream, cancellationToken).ConfigureAwait(false);
            if (parsed && TwoCTwoPMapping.TryReadVerifiedPayload(root, secretKey, out var payload))
            {
                return payload;
            }

            // 2C2P reports some request-level rejections (bad merchant, bad signature) as a plain, unsigned
            // { "respCode": .., "respDesc": .. } body instead of the usual signed payload envelope.
            if (parsed && TwoCTwoPMapping.TryReadPlainError(root, out var respCode, out var respDesc))
            {
                throw new PaymentApiException(
                    TwoCTwoPMapping.ToFailureKind((int)response.StatusCode, respCode), respCode, (int)response.StatusCode, respDesc);
            }

            throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
        }
    }

    private static PaymentApiException Refuse(string code, string message) =>
        new(FailureKind.Validation, code, httpStatus: 0, message);

    private sealed class PayloadEnvelope
    {
        [JsonPropertyName("payload")]
        public required string Payload { get; init; }
    }
}
