using System.Net;

namespace Themia.Payments.TwoCTwoP.Internal;

/// <summary>Sends a 2C2P PGW 4.3 request with retry. Unlike Beam, 2C2P carries no auth header or idempotency
/// header — the JWT signature over the body is the only authentication, and an idempotency key (when the
/// caller wants one) travels inside that same signed body (spec §7b, §5).</summary>
internal static class TwoCTwoPHttp
{
    /// <summary>The maximum number of attempts <see cref="SendWithRetryAsync"/> makes for one logical call.</summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// Posts to <paramref name="path"/>, retrying on a transient failure — <c>5xx</c>, <c>429</c>,
    /// <see cref="HttpRequestException"/>, or a <see cref="TaskCanceledException"/> that is not the caller's own
    /// cancellation — up to <see cref="MaxAttempts"/> times, with exponential backoff plus jitter. Never retries a <c>4xx</c>.
    /// When the last attempt still fails at the transport, throws <see cref="PaymentApiException"/>
    /// (<see cref="FailureKind.Transient"/>, <c>transport_error</c> or <c>timeout</c>); the caller's own
    /// cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="httpClient">The client to send on.</param>
    /// <param name="path">The request path, relative to the client's base address.</param>
    /// <param name="contentFactory">
    /// Builds fresh <see cref="HttpContent"/> for each attempt — an <see cref="HttpRequestMessage"/> (and the
    /// content it owns) cannot be resent once disposed. The JWT body itself, including any idempotency key, is
    /// built once by the caller before the retry loop; this only re-wraps the same bytes.
    /// </param>
    /// <param name="cancellationToken">Propagated to every attempt and to the backoff delay.</param>
    public static async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpClient httpClient,
        string path,
        Func<HttpContent> contentFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var isLastAttempt = attempt == MaxAttempts - 1;
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = contentFactory() };
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
