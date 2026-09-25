using System.Net.Http.Headers;
using System.Text;

namespace Themia.Payments.Beam.Internal;

/// <summary>Builds Beam requests: Basic auth, the optional partner header, and the idempotency key.</summary>
internal static class BeamHttp
{
    public const string IdempotencyHeader = "x-beam-idempotency-key";
    public const string PartnerHeader = "X-Beam-Partner-ID";

    public static HttpRequestMessage Create(
        HttpMethod method, string path, BeamOptions options, HttpContent? content = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.MerchantId}:{options.ApiKey}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        if (!string.IsNullOrWhiteSpace(options.PartnerId))
        {
            request.Headers.TryAddWithoutValidation(PartnerHeader, options.PartnerId);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation(IdempotencyHeader, idempotencyKey);
        }

        return request;
    }
}
