using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Themia.Payments.Beam.Internal;

/// <summary>
/// Beam's payment-link create, get and disable calls. Lives here, not inline in <see cref="BeamPaymentGateway"/>,
/// because Task 11's public <c>BeamPaymentClient</c> exposes the same calls directly.
/// </summary>
internal static class BeamPaymentLinks
{
    /// <summary>Creates a payment link offering exactly <paramref name="allowed"/>'s methods.</summary>
    public static async Task<ChargeCreation> CreateAsync(
        HttpClient httpClient,
        BeamOptions options,
        CreateChargeRequest request,
        IReadOnlyList<PaymentMethod> allowed,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(BuildRequestBody(request, allowed), BeamMapping.SerializeOptions);
        var idempotencyKey = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");
        var (id, url) = await PostCreateAsync(httpClient, options, payload, idempotencyKey, cancellationToken).ConfigureAwait(false);
        return new ChargeCreation(id, PaymentStatus.Pending, new NextAction.Redirect(new Uri(url)));
    }

    /// <summary>
    /// Creates a payment link from Task 11's <see cref="BeamPaymentClient"/>'s own request, which carries
    /// fields <see cref="CreateChargeRequest"/> has no room for (<c>cancelUrl</c>, the collection flags).
    /// Shares <see cref="PostCreateAsync"/> with <see cref="CreateAsync"/> rather than parsing the 201
    /// response a second time.
    /// </summary>
    public static Task<(string Id, string Url)> CreateLinkAsync(
        HttpClient httpClient,
        BeamOptions options,
        BeamPaymentLinkRequest request,
        IReadOnlyList<PaymentMethod> allowed,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(BuildRequestBody(request, allowed), BeamMapping.SerializeOptions);
        return PostCreateAsync(httpClient, options, payload, idempotencyKey, cancellationToken);
    }

    /// <summary>Disables a payment link. Beam reports success as HTTP 202.</summary>
    public static async Task DisableAsync(
        HttpClient httpClient, BeamOptions options, string linkId, CancellationToken cancellationToken)
    {
        var idempotencyKey = Guid.NewGuid().ToString("N");
        using var response = await BeamHttp.SendWithRetryAsync(
            httpClient,
            HttpMethod.Patch,
            $"/api/v1/payment-links/{Uri.EscapeDataString(linkId)}/disable",
            options,
            contentFactory: null,
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var (parsed, root) = await BeamMapping.TryParseAsync(stream, cancellationToken).ConfigureAwait(false);
                throw BeamMapping.ToApiException(response.StatusCode, parsed ? root : null);
            }
        }
    }

    /// <summary>
    /// POSTs a payment-link create body and parses Beam's <c>{ "id", "url" }</c> response — the one place
    /// that response is parsed, shared by <see cref="CreateAsync"/> and <see cref="CreateLinkAsync"/>.
    /// </summary>
    private static async Task<(string Id, string Url)> PostCreateAsync(
        HttpClient httpClient, BeamOptions options, string payload, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var response = await BeamHttp.SendWithRetryAsync(
            httpClient,
            HttpMethod.Post,
            "/api/v1/payment-links",
            options,
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

            // The create response is { "id": "...", "url": "..." } (Beam's CreatePaymentLinkResponse) —
            // "paymentLinkId" belongs to the GET response and the webhook payload, not to create.
            if (!parsed ||
                root.ValueKind != JsonValueKind.Object ||
                !BeamMapping.TryGetNonEmptyString(root, "id", out var id) ||
                !BeamMapping.TryGetNonEmptyString(root, "url", out var url))
            {
                throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
            }

            return (id, url);
        }
    }

    /// <summary>Reads a payment link's current status and order — Beam's GET response, not the create shape.</summary>
    /// <exception cref="PaymentApiException">
    /// Beam rejected the call (including a 404 for an id that is not a payment link either), or the 2xx body was
    /// missing a field this method needs.
    /// </exception>
    public static async Task<PaymentLinkStatus> GetAsync(
        HttpClient httpClient, BeamOptions options, string linkId, CancellationToken cancellationToken)
    {
        using var response = await BeamHttp.SendWithRetryAsync(
            httpClient,
            HttpMethod.Get,
            $"/api/v1/payment-links/{Uri.EscapeDataString(linkId)}",
            options,
            contentFactory: null,
            idempotencyKey: null,
            cancellationToken).ConfigureAwait(false);

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var (parsed, root) = await BeamMapping.TryParseAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw BeamMapping.ToApiException(response.StatusCode, parsed ? root : null);
            }

            if (!parsed ||
                root.ValueKind != JsonValueKind.Object ||
                !BeamMapping.TryGetNonEmptyString(root, "status", out var status) ||
                !root.TryGetProperty("order", out var order) ||
                order.ValueKind != JsonValueKind.Object ||
                !order.TryGetProperty("netAmount", out var netAmountElement) ||
                netAmountElement.ValueKind != JsonValueKind.Number ||
                !netAmountElement.TryGetInt64(out var netAmount) ||
                !BeamMapping.TryGetNonEmptyString(order, "currency", out var currency) ||
                !BeamMapping.TryGetNonEmptyString(order, "referenceId", out var referenceId))
            {
                throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
            }

            // "url" and "expiresAt" are optional here: BeamCharges.GetByPaymentLinkIdAsync (Task 8) never
            // needed them, so existing fixtures omit them — only BeamPaymentClient.GetPaymentLinkAsync
            // (Task 11) requires "url", and it checks for that itself rather than failing this shared parse.
            var url = BeamMapping.TryGetNonEmptyString(root, "url", out var urlValue) ? urlValue : null;

            DateTimeOffset? expiresAt = null;
            if (root.TryGetProperty("expiresAt", out var expiresAtElement) &&
                expiresAtElement.ValueKind == JsonValueKind.String)
            {
                if (!expiresAtElement.TryGetDateTimeOffset(out var parsedExpiresAt))
                {
                    throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
                }

                expiresAt = parsedExpiresAt;
            }

            return new PaymentLinkStatus(status, url, netAmount, currency, referenceId, expiresAt);
        }
    }

    /// <summary>A payment link's GET response, reduced to what its two callers need.</summary>
    /// <param name="Status"><c>ACTIVE</c>, <c>PAID</c>, <c>VOIDED</c>, <c>REFUNDED</c>, <c>EXPIRED</c> or <c>DISABLED</c>.</param>
    /// <param name="Url">The link's checkout URL, when Beam sent one.</param>
    /// <param name="NetAmount">The order's amount, in minor units.</param>
    /// <param name="Currency">The order's currency.</param>
    /// <param name="ReferenceId">The order's reference id.</param>
    /// <param name="ExpiresAt">When the link stops accepting payment, when it has an expiry.</param>
    internal readonly record struct PaymentLinkStatus(
        string Status, string? Url, long NetAmount, string Currency, string ReferenceId, DateTimeOffset? ExpiresAt);

    private static PaymentLinkRequestBody BuildRequestBody(CreateChargeRequest request, IReadOnlyList<PaymentMethod> allowed) =>
        new()
        {
            Order = new PaymentLinkOrder
            {
                NetAmount = request.Amount.MinorUnits,
                Currency = request.Amount.Currency,
                ReferenceId = request.ReferenceId,
                Description = request.Description,
            },
            LinkSettings = BeamMapping.ToLinkSettings(allowed),
            RedirectUrl = request.ReturnUrl,
            ExpiresAt = request.ExpiresAt,
        };

    private static PaymentLinkRequestBody BuildRequestBody(BeamPaymentLinkRequest request, IReadOnlyList<PaymentMethod> allowed) =>
        new()
        {
            Order = new PaymentLinkOrder
            {
                NetAmount = request.Amount.MinorUnits,
                Currency = request.Amount.Currency,
                ReferenceId = request.ReferenceId,
                Description = request.Description,
            },
            LinkSettings = BeamMapping.ToLinkSettings(allowed),
            RedirectUrl = request.RedirectUrl,
            CancelUrl = request.CancelUrl,
            ExpiresAt = request.ExpiresAt,
            CollectPhoneNumber = request.CollectPhoneNumber,
            CollectDeliveryAddress = request.CollectDeliveryAddress,
        };

    private sealed class PaymentLinkRequestBody
    {
        [JsonPropertyName("order")]
        public required PaymentLinkOrder Order { get; init; }

        [JsonPropertyName("linkSettings")]
        public required Dictionary<string, object> LinkSettings { get; init; }

        [JsonPropertyName("redirectUrl")]
        public Uri? RedirectUrl { get; init; }

        [JsonPropertyName("cancelUrl")]
        public Uri? CancelUrl { get; init; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset? ExpiresAt { get; init; }

        [JsonPropertyName("collectPhoneNumber")]
        public bool? CollectPhoneNumber { get; init; }

        [JsonPropertyName("collectDeliveryAddress")]
        public bool? CollectDeliveryAddress { get; init; }
    }

    private sealed class PaymentLinkOrder
    {
        [JsonPropertyName("netAmount")]
        public required long NetAmount { get; init; }

        [JsonPropertyName("currency")]
        public required string Currency { get; init; }

        [JsonPropertyName("referenceId")]
        public required string ReferenceId { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }
    }
}
