using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using Themia.Payments.TwoCTwoP.Internal;

namespace Themia.Payments.TwoCTwoP;

/// <summary>2C2P PGW 4.3 (Redirect API) <see cref="IPaymentGateway"/> adapter.</summary>
/// <remarks>
/// Every request and response body is <c>{"payload": "&lt;JWT&gt;"}</c>, HS256-signed with the merchant secret
/// — there is no bearer or basic header. A signed <c>payload</c> is verified before any of it is trusted, and an
/// unverified payload is never parsed into a result. The one unsigned body read is 2C2P's plain
/// <c>{ "respCode", "respDesc" }</c> request-rejection shape, and only to raise a <see cref="PaymentApiException"/>
/// carrying that code. 2C2P mints no charge id before payment, so
/// <see cref="ChargeCreation.ChargeId"/> and <see cref="Charge.ChargeId"/> are always the caller's own
/// <see cref="CreateChargeRequest.ReferenceId"/> — 2C2P's <c>invoiceNo</c>. Refunds are sent once and never
/// retried (see <see cref="RefundAsync"/>).
/// </remarks>
public sealed class TwoCTwoPPaymentGateway : IPaymentGateway, IRefundStatusGateway, IPaymentGatewayCapabilities, IDisposable
{
    /// <summary>The name this adapter's <see cref="HttpClient"/> is registered under.</summary>
    public const string HttpClientName = "themia-payments-2c2p";

    private const string DoPaymentPath = "/payment/4.3/payment";

    // PromptPay in 2C2P's Payment Channel Matrix: channel THQR (Thai QR), agent PPQR.
    private const string PromptPayChannelCode = "THQR";
    private const string PromptPayAgentCode = "PPQR";

    // 2C2P's "success" respCode on the Payment Maintenance API (the PGW 4.3 API uses 0000).
    private const string RefundAcceptedRespCode = "00";

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
    private readonly object refundKeysLock = new();
    private TwoCTwoPRefundKeys? refundKeys;

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

    /// <summary>Releases the refund keys, if they were loaded.</summary>
    public void Dispose()
    {
        lock (refundKeysLock)
        {
            refundKeys?.Dispose();
            refundKeys = null;
        }
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

        if (request.ExpiresAt is { } expiresAt)
        {
            // 2C2P's paymentExpiry is a bare local timestamp, read in the same zone as its transactionDateTime.
            claims["paymentExpiry"] = expiresAt.ToOffset(twoCTwoPOptions.TransactionTimeOffset)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
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

        if (!TwoCTwoPMapping.TryGetNonEmptyString(verified, "webPaymentUrl", out var webPaymentUrlText) ||
            !TwoCTwoPMapping.TryParseHttpUrl(webPaymentUrlText, out var webPaymentUrl))
        {
            throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
        }

        NextAction action = new NextAction.Redirect(webPaymentUrl);
        if (request.PresentationMode != PresentationMode.HostedPage &&
            allowed.Count == 1 && allowed[0] == PaymentMethod.QrPromptPay &&
            TwoCTwoPMapping.TryGetNonEmptyString(verified, "paymentToken", out var paymentToken) &&
            await TryCreateQrAsync(httpClient, paymentToken, twoCTwoPOptions.SecretKey, request, cancellationToken).ConfigureAwait(false)
                is { } showQr)
        {
            action = showQr;
        }

        return new ChargeCreation(request.ReferenceId, PaymentStatus.Pending, action);
    }

    /// <summary>
    /// For a PromptPay-only charge, asks 2C2P's Do Payment API for the QR so it can be shown in the page instead
    /// of redirecting to the hosted page. Sent once, never retried. Any doubt — a failed call, another flow code,
    /// an untrusted or unreadable image — returns <see langword="null"/> and the caller redirects to the hosted
    /// page, which can still take the payment.
    /// </summary>
    private static async Task<NextAction.ShowQr?> TryCreateQrAsync(
        HttpClient httpClient, string paymentToken, string secretKey, CreateChargeRequest request, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            paymentToken,
            payment = new
            {
                code = new { channelCode = PromptPayChannelCode, agentCode = PromptPayAgentCode },
                data = new { },
            },
        });

        string responseText;
        try
        {
            using var response = await httpClient.PostAsync(
                DoPaymentPath, new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (!TryParseDoPaymentAnswer(responseText, secretKey, out var answer) ||
            !TwoCTwoPQrCode.TryReadQrAnswer(answer, out var imageUrl, out var expiresIn))
        {
            return null;
        }

        var png = await TwoCTwoPQrCode.TryDownloadPngAsync(httpClient, imageUrl, cancellationToken).ConfigureAwait(false);
        if (png is null)
        {
            return null;
        }

        // The earlier of 2C2P's countdown and the caller's own deadline.
        var expiry = request.ExpiresAt;
        if (expiresIn is { } timer)
        {
            var countdownEnd = DateTimeOffset.UtcNow + timer;
            expiry = expiry is { } own && own < countdownEnd ? own : countdownEnd;
        }

        return new NextAction.ShowQr(png, RawPayload: null, expiry);
    }

    // The documentation shows Do Payment answering plain JSON while stating that responses are JWT: take either,
    // but a body that carries a signed payload must verify before any of it is read.
    private static bool TryParseDoPaymentAnswer(string responseText, string secretKey, out JsonElement answer)
    {
        answer = default;
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("payload", out _))
            {
                return TwoCTwoPMapping.TryReadVerifiedPayload(root, secretKey, out answer);
            }

            answer = root.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="charge"/> carries neither a reference id nor a provider charge id.</exception>
    /// <exception cref="PaymentApiException">2C2P found no such transaction (<see cref="FailureKind.NotFound"/>),
    /// answered with <c>0999</c> or a <c>respCode</c> this package does not know (<see cref="FailureKind.Transient"/>,
    /// the raw code as <see cref="PaymentApiException.ProviderCode"/> — retry the read), or rejected the call.</exception>
    /// <remarks>2C2P's Payment Inquiry reads by <c>invoiceNo</c>, which is the app's own reference id — the
    /// provider charge id is only a fallback, and only because 2C2P's invoice number and charge id are the same value.</remarks>
    public async Task<Charge> GetChargeAsync(ChargeRef charge, CancellationToken cancellationToken = default)
    {
        ChargeRefValidator.Validate(charge);
        var invoiceNo = !string.IsNullOrWhiteSpace(charge.ReferenceId) ? charge.ReferenceId : charge.ProviderChargeId!;

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

        var respDesc = TwoCTwoPMapping.TryGetNonEmptyString(verified, "respDesc", out var desc) ? desc : null;
        if (respCode == "2002")
        {
            throw new PaymentApiException(FailureKind.NotFound, respCode, (int)response.StatusCode, respDesc);
        }

        // 0999 (2C2P system error) or a code this package does not know: the inquiry did not say whether the
        // shopper paid, so this is a failed call to retry, never a failed payment an app might act on.
        if (!TwoCTwoPMapping.IsKnownRespCode(respCode))
        {
            throw new PaymentApiException(FailureKind.Transient, respCode, (int)response.StatusCode, respDesc);
        }

        return TwoCTwoPMapping.ToCharge(verified, invoiceNo, respCode, (int)response.StatusCode, twoCTwoPOptions.TransactionTimeOffset);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Needs <see cref="TwoCTwoPOptions.MerchantPrivateKeyPem"/> and <see cref="TwoCTwoPOptions.ProviderPublicCertificatePem"/>; without them it refuses
    /// with <c>refund_not_supported</c> and sends nothing. The refund goes through 2C2P's Payment Maintenance API (XML in a JWE inside a JWS, see the
    /// README). It is sent ONCE, never retried: whether 2C2P replays an <c>idempotencyID</c> is not established, so a timeout or 5xx throws
    /// <c>refund_outcome_unknown</c> (so does cancelling the token once the request is on the wire) and the caller must check the 2C2P merchant portal before refunding again. 2C2P is the only guard against
    /// refunding more than was paid; serialize refunds per charge.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null (on the returned task).</exception>
    /// <exception cref="ArgumentException">The request's charge reference names no charge (on the returned task).</exception>
    /// <exception cref="PaymentApiException">The request was refused locally (invalid key, amount not positive, charge not paid, currency or amount
    /// out of range, refunds not configured), 2C2P rejected it, or its outcome is unknown (<c>refund_outcome_unknown</c>).</exception>
    public async Task<RefundCreation> RefundAsync(RefundRequest request, CancellationToken cancellationToken = default)
    {
        RefundRequestValidator.Validate(request);

        var twoCTwoPOptions = options.Value;
        if (!TwoCTwoPRefundKeys.IsConfigured(twoCTwoPOptions))
        {
            throw Refuse("refund_not_supported",
                "Refunds are not configured: set MerchantPrivateKeyPem and ProviderPublicCertificatePem, or refund from the 2C2P merchant portal.");
        }

        if (request.Amount is { MinorUnits: <= 0 })
        {
            throw Refuse("refund_amount_invalid", "A refund amount must be greater than zero.");
        }

        var keys = RefundKeys(twoCTwoPOptions);
        var charge = await GetChargeAsync(request.Charge, cancellationToken).ConfigureAwait(false);
        if (charge.Status != PaymentStatus.Succeeded)
        {
            throw Refuse("charge_not_refundable", "Only a paid charge can be refunded.");
        }

        var amount = request.Amount ?? charge.Amount;
        if (!string.Equals(amount.Currency, charge.Amount.Currency, StringComparison.Ordinal))
        {
            throw Refuse("refund_currency_mismatch", "The refund currency differs from the charge's.");
        }

        if (amount.MinorUnits > charge.Amount.MinorUnits)
        {
            throw Refuse("refund_amount_exceeds_charge", "A refund cannot exceed the charge's amount.");
        }

        var decimalAmount = TwoCTwoPMapping.ToDecimalAmount(amount);
        var invoiceNo = charge.ReferenceId;
        var idempotencyId = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");
        var xml = TwoCTwoPRefundXml.BuildRequest(
            twoCTwoPOptions.MerchantId, invoiceNo, decimalAmount, idempotencyId,
            DateTimeOffset.UtcNow.ToOffset(twoCTwoPOptions.TransactionTimeOffset), twoCTwoPOptions.RefundNotifyUrl);
        var jws = JweJws.Seal(xml, keys.ProviderPublic, keys.MerchantPrivate);

        var verified = await SendActionAsync(
            jws, twoCTwoPOptions, keys, TwoCTwoPRefundXml.ParseResponse, OutcomeUnknown, OutcomeUnknown, cancellationToken).ConfigureAwait(false);
        return MapRefund(verified, invoiceNo, twoCTwoPOptions.MerchantId, idempotencyId);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Asks 2C2P's Payment Maintenance API for the refunds of the charge's invoice (<c>processType</c> <c>RS</c>); this is how a
    /// <see cref="PaymentStatus.Pending"/> refund is followed to its end. 2C2P can also notify an asynchronous refund (see
    /// <see cref="TwoCTwoPRefundNotificationVerifier"/>), but that event only says when to call this method again. Needs the same keys as
    /// <see cref="RefundAsync"/> and refuses with <c>refund_not_supported</c> without them. Match a record to the refund you sent
    /// by <see cref="RefundRecord.IdempotencyKey"/>: the key given to <see cref="RefundAsync"/> is sent as <c>userDefined1</c> and
    /// echoed back (a refund made in the 2C2P portal has none). Read-only and repeatable; a failure to reach 2C2P is
    /// <see cref="FailureKind.Transient"/>. Not verified against the 2C2P sandbox.
    /// </remarks>
    public async Task<IReadOnlyList<RefundRecord>> GetRefundsAsync(ChargeRef charge, CancellationToken cancellationToken = default)
    {
        var twoCTwoPOptions = options.Value;
        if (!TwoCTwoPRefundKeys.IsConfigured(twoCTwoPOptions))
        {
            throw Refuse("refund_not_supported",
                "Refund status is not configured: set MerchantPrivateKeyPem and ProviderPublicCertificatePem.");
        }

        var keys = RefundKeys(twoCTwoPOptions);
        var paid = await GetChargeAsync(charge, cancellationToken).ConfigureAwait(false);
        var jws = JweJws.Seal(
            TwoCTwoPRefundXml.BuildStatusRequest(twoCTwoPOptions.MerchantId, paid.ReferenceId, DateTimeOffset.UtcNow.ToOffset(twoCTwoPOptions.TransactionTimeOffset)),
            keys.ProviderPublic, keys.MerchantPrivate);

        var answer = await SendActionAsync(
            jws, twoCTwoPOptions, keys, TwoCTwoPRefundXml.ParseStatusResponse, StatusUnavailable, StatusUnreadable, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(answer.InvoiceNo, paid.ReferenceId, StringComparison.Ordinal)
            || (answer.MerchantId is not null && !string.Equals(answer.MerchantId, twoCTwoPOptions.MerchantId, StringComparison.Ordinal)))
        {
            throw StatusUnreadable("The refund status response is for a different transaction.", 200, null);
        }

        if (answer.RespCode != RefundAcceptedRespCode)
        {
            throw new PaymentApiException(FailureKind.Unknown, answer.RespCode, 200, answer.RespDesc);
        }

        return answer.Refunds.Select(row => MapRefundRow(row, paid.Amount.Currency, twoCTwoPOptions.TransactionTimeOffset)).ToList();
    }

    private static RefundRecord MapRefundRow(TwoCTwoPRefundXml.RefundRow row, string currency, TimeSpan offset)
    {
        if (!TwoCTwoPMapping.TryFromDecimalAmount(row.Amount, currency, out var amount))
        {
            throw StatusUnreadable("A refund amount in the refund status response is not a valid amount.", 200, null);
        }

        // yyyyMMddHHmmss, in the same zone as the transaction's own timestamps.
        DateTimeOffset? at = DateTime.TryParseExact(row.DateTime, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? new DateTimeOffset(parsed, offset)
            : null;
        return new RefundRecord(row.ReferenceNo, amount, MapRefundStatus(row.Status), at, row.UserDefined1);
    }

    private TwoCTwoPRefundKeys RefundKeys(TwoCTwoPOptions twoCTwoPOptions)
    {
        lock (refundKeysLock)
        {
            if (refundKeys is not null)
            {
                return refundKeys;
            }

            if (!TwoCTwoPRefundKeys.TryCreate(twoCTwoPOptions, TimeProvider.System, out var keys, out var error))
            {
                throw Refuse("refund_keys_invalid", error!);
            }

            return refundKeys = keys!;
        }
    }

    /// <summary>
    /// One attempt, no retry, on the Payment Action endpoint. <paramref name="uncertain"/> builds the error for a transport failure
    /// or a 5xx/429/408, <paramref name="unreadable"/> for a response that cannot be opened or parsed: a refund answers both with
    /// <c>refund_outcome_unknown</c>, a read-only inquiry with something it is safe to retry.
    /// </summary>
    private async Task<T> SendActionAsync<T>(
        string jws, TwoCTwoPOptions twoCTwoPOptions, TwoCTwoPRefundKeys keys, Func<string, T?> parse,
        Func<string, int, Exception?, PaymentApiException> uncertain, Func<string, int, Exception?, PaymentApiException> unreadable,
        CancellationToken cancellationToken)
        where T : class
    {
        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        using var content = new StringContent(jws, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/*+json");
        using var message = new HttpRequestMessage(HttpMethod.Post, TwoCTwoPOptions.ActionUriFor(twoCTwoPOptions.Environment)) { Content = content };
        message.Headers.Accept.ParseAdd("text/plain");

        string body;
        int status;
        try
        {
            using var response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Includes the caller's own cancellation: once the request is on the wire the refund may have been made,
            // so a bare OperationCanceledException would read as "nothing happened" and invite a second refund.
            throw uncertain("The request may or may not have reached 2C2P.", 0, ex);
        }

        if (status is >= 500 or 429 or 408)
        {
            throw uncertain("2C2P answered with a server error.", status, null);
        }

        if (status is >= 400)
        {
            throw new PaymentApiException(TwoCTwoPMapping.ToFailureKindForStatus(status), $"HTTP_{status}", status);
        }

        if (!JweJws.TryOpen(body, keys.ProviderPublic, keys.MerchantPrivate, out var xml) || parse(xml) is not { } parsed)
        {
            throw unreadable("The response could not be verified.", status, null);
        }

        return parsed;
    }

    private static RefundCreation MapRefund(TwoCTwoPRefundXml.Response response, string invoiceNo, string merchantId, string idempotencyId)
    {
        // A genuinely signed response for another refund must not read as this one.
        if (!string.Equals(response.InvoiceNo, invoiceNo, StringComparison.Ordinal)
            || (response.MerchantId is not null && !string.Equals(response.MerchantId, merchantId, StringComparison.Ordinal))
            || (response.IdempotencyId is not null && !string.Equals(response.IdempotencyId, idempotencyId, StringComparison.Ordinal)))
        {
            throw OutcomeUnknown("The refund response is for a different transaction.", 200);
        }

        if (response.RespCode != RefundAcceptedRespCode)
        {
            throw OutcomeUnknown($"2C2P answered respCode {response.RespCode} ({response.RespDesc}); the refund may not have been made.", 200);
        }

        // respCode 00 means the request was accepted; the refund's own status says how far it got. The statuses are
        // 2C2P's "Payment Maintenance Status Codes" (https://developer.2c2p.com/docs/response-code-payment-maintenance-status-code):
        // RF "Refund confirmed"; RP "Refund Pending"; RFF "Refund Failed"; RR "Refund Rejected"; RR1 "Refund Rejected -
        // insufficient balance"; RR2 "invalid bank information"; RR3 "bank account mismatch". A status this package does
        // not know stays Pending, never Succeeded.
        return new RefundCreation(null, MapRefundStatus(response.Status));
    }

    internal static PaymentStatus MapRefundStatus(string? status) => status switch
    {
        "RF" => PaymentStatus.Succeeded,
        "RFF" or "RR" or "RR1" or "RR2" or "RR3" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending,
    };

    private static PaymentApiException OutcomeUnknown(string message, int httpStatus, Exception? inner = null) =>
        new(FailureKind.Unknown, "refund_outcome_unknown", httpStatus,
            message + " The refund may or may not have been made. Check the 2C2P merchant portal before refunding again.", inner);

    /// <summary>Reading is repeatable, so a transport failure or 5xx is plain <see cref="FailureKind.Transient"/>.</summary>
    private static PaymentApiException StatusUnavailable(string message, int httpStatus, Exception? inner) =>
        new(FailureKind.Transient, "refund_status_unavailable", httpStatus, message, inner);

    private static PaymentApiException StatusUnreadable(string message, int httpStatus, Exception? inner) =>
        new(FailureKind.Unknown, "malformed_response", httpStatus, message, inner);

    /// <summary>
    /// Parses the response body and returns its verified <c>payload</c> contents. Throws for anything else: a
    /// plain, unsigned <c>respCode</c> error body becomes that code; otherwise a non-2xx becomes
    /// <c>HTTP_{status}</c> classified by status, and a 2xx with no verifiable <c>payload</c> is
    /// <c>malformed_response</c> (ruling: never parse an unverified payload into a result).
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

            // Nothing 2C2P-shaped (e.g. an HTML page from a proxy or load balancer): classify by the HTTP status,
            // as Beam does for a body with no errorCode, rather than calling a 503 a malformed response.
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                throw new PaymentApiException(TwoCTwoPMapping.ToFailureKindForStatus(status), $"HTTP_{status}", status);
            }

            throw new PaymentApiException(FailureKind.Unknown, "malformed_response", status);
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
