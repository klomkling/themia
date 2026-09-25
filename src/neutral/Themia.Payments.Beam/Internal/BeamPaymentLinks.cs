using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Themia.Payments.Beam.Internal;

/// <summary>
/// Beam's payment-link create call. Lives here, not inline in <see cref="BeamPaymentGateway"/>, because
/// Task 11's public <c>BeamPaymentClient</c> exposes the same call directly.
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
        using var httpRequest = BeamHttp.Create(
            HttpMethod.Post,
            "/api/v1/payment-links",
            options,
            new StringContent(payload, Encoding.UTF8, "application/json"),
            request.IdempotencyKey);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var (parsed, root) = await BeamMapping.TryParseAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw BeamMapping.ToApiException(response.StatusCode, parsed ? root : null);
            }

            var paymentLinkId = root.GetProperty("paymentLinkId").GetString()!;
            var url = root.GetProperty("url").GetString()!;
            return new ChargeCreation(paymentLinkId, PaymentStatus.Pending, new NextAction.Redirect(new Uri(url)));
        }
    }

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

    private sealed class PaymentLinkRequestBody
    {
        [JsonPropertyName("order")]
        public required PaymentLinkOrder Order { get; init; }

        [JsonPropertyName("linkSettings")]
        public required Dictionary<string, object> LinkSettings { get; init; }

        [JsonPropertyName("redirectUrl")]
        public Uri? RedirectUrl { get; init; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset? ExpiresAt { get; init; }
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
