using System.Globalization;
using System.Text.Json;

namespace Themia.Payments.TwoCTwoP.Internal;

/// <summary>
/// Reads 2C2P's Do Payment answer for a PromptPay QR (payment flow code <c>1005</c>, "Pending for user scan
/// QR") and fetches the QR image it points at. Both steps are defensive: the image URL is followed only when it
/// is an <c>https</c> address on a <c>2c2p.com</c> host, the download is size-capped and must be a PNG, and any
/// doubt yields <see langword="null"/> so the caller falls back to the hosted payment page.
/// </summary>
internal static class TwoCTwoPQrCode
{
    /// <summary>2C2P's payment flow code for "display the generated QR and wait for the shopper to pay".</summary>
    private const string QrFlowRespCode = "1005";

    private const string UrlDataType = "URL";

    private const string TrustedHost = "2c2p.com";

    private const int MaxImageBytes = 256 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Extracts the QR image URL and the expiry countdown from a Do Payment answer.</summary>
    /// <returns><see langword="false"/> unless the answer is flow code 1005 with a trusted <c>https</c> PNG URL.</returns>
    public static bool TryReadQrAnswer(JsonElement answer, out Uri imageUrl, out TimeSpan? expiresIn)
    {
        imageUrl = null!;
        expiresIn = null;

        if (!TwoCTwoPMapping.TryGetNonEmptyString(answer, "respCode", out var respCode) || respCode != QrFlowRespCode ||
            !TwoCTwoPMapping.TryGetNonEmptyString(answer, "type", out var type) || !string.Equals(type, UrlDataType, StringComparison.OrdinalIgnoreCase) ||
            !TwoCTwoPMapping.TryGetNonEmptyString(answer, "data", out var data) ||
            !Uri.TryCreate(data, UriKind.Absolute, out var url) || !IsTrusted(url))
        {
            return false;
        }

        imageUrl = url;
        expiresIn = ReadExpiryTimer(answer);
        return true;
    }

    /// <summary>Downloads the QR image from <paramref name="imageUrl"/>.</summary>
    /// <returns>The PNG bytes, or <see langword="null"/> when the download failed, ended on another host, was
    /// over the size cap, or is not a PNG.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static async Task<byte[]?> TryDownloadPngAsync(HttpClient httpClient, Uri imageUrl, CancellationToken cancellationToken)
    {
        // HttpCompletionOption.ResponseHeadersRead hands back before the body arrives, so the client's Timeout no
        // longer covers the streamed read: apply it to the whole download here, or a slow drip would hang the charge.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (httpClient.Timeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(httpClient.Timeout);
        }

        try
        {
            using var response = await httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            // A redirect is followed by the handler; refuse to trust an image that came from somewhere else.
            var finalUrl = response.RequestMessage?.RequestUri ?? imageUrl;
            if (!response.IsSuccessStatusCode || !IsTrusted(finalUrl) || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                return null;
            }

            var bytes = await ReadCappedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            return bytes is not null && IsPng(bytes) ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException ||
            (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A refused connection, a body cut off mid-stream (HttpIOException is an IOException) or the timeout:
            // the caller falls back to the hosted page rather than failing a charge that was already created.
            return null;
        }
    }

    private static bool IsTrusted(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps &&
        (url.Host.Equals(TrustedHost, StringComparison.OrdinalIgnoreCase) ||
         url.Host.EndsWith("." + TrustedHost, StringComparison.OrdinalIgnoreCase));

    private static bool IsPng(byte[] bytes) => bytes.AsSpan().StartsWith(PngSignature);

    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static TimeSpan? ReadExpiryTimer(JsonElement answer)
    {
        if (!answer.TryGetProperty("expiryTimer", out var timer))
        {
            return null;
        }

        // The documentation types expiryTimer as numeric milliseconds; tolerate a numeric string too.
        var milliseconds = timer.ValueKind switch
        {
            JsonValueKind.Number when timer.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(timer.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0L,
        };
        return milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : null;
    }
}
