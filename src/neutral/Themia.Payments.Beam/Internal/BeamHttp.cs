using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Themia.Payments.Beam.Internal;

/// <summary>Builds Beam requests: Basic auth, the optional partner header, and the idempotency key.</summary>
internal static class BeamHttp
{
    public const string IdempotencyHeader = "x-beam-idempotency-key";
    public const string PartnerHeader = "X-Beam-Partner-ID";

    /// <summary>The maximum number of attempts <see cref="SendWithRetryAsync"/> makes for one logical call.</summary>
    private const int MaxAttempts = 3;

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

    /// <summary>
    /// Sends a request, retrying on a transient failure — <c>5xx</c>, <c>429</c>, <see cref="HttpRequestException"/>,
    /// or a <see cref="TaskCanceledException"/> that is not the caller's own cancellation — up to
    /// <see cref="MaxAttempts"/> times, with exponential backoff plus jitter. Never retries a <c>4xx</c>.
    /// When the last attempt still fails at the transport, throws <see cref="PaymentApiException"/>
    /// (<see cref="FailureKind.Transient"/>, <c>transport_error</c> or <c>timeout</c>); the caller's own
    /// cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="httpClient">The client to send on.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The request path, relative to <paramref name="options"/>'s base address.</param>
    /// <param name="options">The Beam credentials and environment.</param>
    /// <param name="contentFactory">
    /// Builds fresh <see cref="HttpContent"/> for each attempt, or null for a body-less request — an
    /// <see cref="HttpRequestMessage"/> (and the content it owns) cannot be resent once disposed.
    /// </param>
    /// <param name="idempotencyKey">
    /// The key to carry on every attempt, resolved once by the caller before the retry loop starts, or null to
    /// send none (Beam ignores it on GET).
    /// </param>
    /// <param name="cancellationToken">Propagated to every attempt and to the backoff delay.</param>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient httpClient,
        HttpMethod method,
        string path,
        BeamOptions options,
        Func<HttpContent>? contentFactory,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var isLastAttempt = attempt == MaxAttempts - 1;
            using var request = Create(method, path, options, contentFactory?.Invoke(), idempotencyKey);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                if (isLastAttempt)
                {
                    throw new PaymentApiException(
                        FailureKind.Transient, "transport_error", 0, $"The provider could not be reached after {MaxAttempts} attempts.", ex);
                }

                await Task.Delay(BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Not the caller's own cancellation (that propagates as-is): the per-request timeout fired.
                if (isLastAttempt)
                {
                    throw new PaymentApiException(
                        FailureKind.Transient, "timeout", 0, $"The provider did not respond within the timeout after {MaxAttempts} attempts.", ex);
                }

                await Task.Delay(BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (isLastAttempt || !IsRetryable(response.StatusCode))
            {
                return response;
            }

            response.Dispose();
            await Task.Delay(BackoffDelay(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) => (int)status >= 500 || status == HttpStatusCode.TooManyRequests;

    private static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromMilliseconds((200 * Math.Pow(2, attempt)) + Random.Shared.Next(0, 101));
}
