using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Themia.Payments.Beam.Internal;

namespace Themia.Payments.Beam;

/// <summary>A request to create a Beam hosted checkout link.</summary>
public sealed record BeamPaymentLinkRequest
{
    /// <summary>The amount. Must be greater than zero.</summary>
    public required Money Amount { get; init; }

    /// <summary>The app's own order id.</summary>
    public required string ReferenceId { get; init; }

    /// <summary>The methods this link may offer. Cannot be empty or contain a duplicate.</summary>
    public required IReadOnlyList<PaymentMethod> AllowedMethods { get; init; }

    /// <summary>What the shopper sees the payment called.</summary>
    public string? Description { get; init; }

    /// <summary>Where the shopper lands after paying.</summary>
    public Uri? RedirectUrl { get; init; }

    /// <summary>
    /// Where the shopper lands if they cancel. When set, Beam's checkout page renders a cancel button.
    /// </summary>
    public Uri? CancelUrl { get; init; }

    /// <summary>When the link stops being payable. Must be in the future.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Whether the checkout page asks for the shopper's phone number.</summary>
    public bool CollectPhoneNumber { get; init; }

    /// <summary>Whether the checkout page asks for a delivery address.</summary>
    public bool CollectDeliveryAddress { get; init; }

    /// <summary>
    /// Stable across retries of the same logical operation. Generated per call when absent.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>A Beam hosted checkout link.</summary>
/// <param name="Id">Beam's handle for the link. Also usable as a charge's <c>sourceId</c>.</param>
/// <param name="Url">Where to send the shopper to pay.</param>
/// <param name="Status">The link's current state.</param>
/// <param name="Amount">The order amount.</param>
/// <param name="ReferenceId">The app's own order id.</param>
/// <param name="ExpiresAt">When the link stops being payable, when it has an expiry.</param>
public sealed record BeamPaymentLink(
    string Id, Uri Url, BeamPaymentLinkStatus Status, Money Amount, string? ReferenceId, DateTimeOffset? ExpiresAt);

/// <summary>Where a Beam payment link currently is.</summary>
public enum BeamPaymentLinkStatus
{
    /// <summary>Payable.</summary>
    Active,

    /// <summary>Disabled by a caller; no longer payable.</summary>
    Disabled,

    /// <summary>Past its expiry; no longer payable.</summary>
    Expired,

    /// <summary>Paid.</summary>
    Paid,

    /// <summary>Voided.</summary>
    Voided,

    /// <summary>Refunded.</summary>
    Refunded,
}

/// <summary>Which of the two success cases a slip verification hit.</summary>
public enum BeamSlipVerificationResult
{
    /// <summary>The charge was PENDING and is now SUCCEEDED. Beam has sent the usual webhooks.</summary>
    UpdatedToSucceeded,

    /// <summary>The charge was already SUCCEEDED; nothing changed.</summary>
    AlreadySucceeded,
}

/// <summary>What a slip verification matched.</summary>
/// <param name="ChargeId">The charge the slip matched.</param>
/// <param name="Result">Which success case it was.</param>
public sealed record BeamSlipVerification(string ChargeId, BeamSlipVerificationResult Result);

/// <summary>Beam features with no counterpart in <see cref="IPaymentGateway"/>.</summary>
/// <remarks>
/// Reaching for this type is how an app says out loud that it is now tied to Beam. Two traps the wrappers
/// enforce: the QR to send is the one <b>on the slip</b>, not the one the shopper scanned to pay; and
/// <c>verificationResult</c> never reports failure — an unreadable or unmatched slip is a 4xx, so the HTTP
/// status is the answer. A slip can only match a charge <b>Beam itself created</b>, so this cannot confirm a
/// QR rendered offline by <c>Themia.PromptPay</c>.
/// </remarks>
public sealed class BeamPaymentClient
{
    private const long MaxSlipImageBytes = 2 * 1024 * 1024;

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<BeamOptions> options;
    private readonly PaymentMethodGate gate;

    /// <summary>Creates the client.</summary>
    /// <param name="httpClientFactory">Used to create the named <see cref="HttpClient"/> Beam's gateway also uses.</param>
    /// <param name="options">The Beam credentials and environment.</param>
    /// <param name="gate">Applies the shared method policy before creating a link.</param>
    /// <exception cref="ArgumentNullException">Any parameter is null.</exception>
    public BeamPaymentClient(IHttpClientFactory httpClientFactory, IOptions<BeamOptions> options, PaymentMethodGate gate)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(gate);

        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.gate = gate;
    }

    /// <summary>Creates a hosted checkout link.</summary>
    /// <param name="request">The link to create.</param>
    /// <param name="cancellationToken">Propagated to the HTTP call.</param>
    /// <returns>The created link, with <see cref="BeamPaymentLinkStatus.Active"/> — no extra GET is made.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    /// <exception cref="PaymentApiException">
    /// The request was refused locally, the policy left no allowed method, or Beam rejected the call.
    /// </exception>
    public async Task<BeamPaymentLink> CreatePaymentLinkAsync(
        BeamPaymentLinkRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateLinkRequest(request);
        var allowed = gate.Apply(request.Amount, request.AllowedMethods);
        var idempotencyKey = request.IdempotencyKey ?? Guid.NewGuid().ToString("N");
        var httpClient = httpClientFactory.CreateClient(BeamPaymentGateway.HttpClientName);

        var (id, url) = await BeamPaymentLinks.CreateLinkAsync(
            httpClient, options.Value, request, allowed, idempotencyKey, cancellationToken).ConfigureAwait(false);

        return new BeamPaymentLink(id, new Uri(url), BeamPaymentLinkStatus.Active, request.Amount, request.ReferenceId, request.ExpiresAt);
    }

    /// <summary>Reads a payment link's current state.</summary>
    /// <param name="paymentLinkId">Beam's id for the link.</param>
    /// <param name="cancellationToken">Propagated to the HTTP call.</param>
    /// <exception cref="ArgumentException"><paramref name="paymentLinkId"/> is null or blank.</exception>
    /// <exception cref="PaymentApiException">
    /// Beam rejected the call, reported a status this package does not recognize, or the 2xx body had no url.
    /// </exception>
    public async Task<BeamPaymentLink> GetPaymentLinkAsync(string paymentLinkId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentLinkId);
        var httpClient = httpClientFactory.CreateClient(BeamPaymentGateway.HttpClientName);

        var link = await BeamPaymentLinks.GetAsync(httpClient, options.Value, paymentLinkId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(link.Url))
        {
            throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)HttpStatusCode.OK);
        }

        return new BeamPaymentLink(
            paymentLinkId,
            new Uri(link.Url),
            ToLinkStatus(link.Status),
            Money.From(link.NetAmount, link.Currency),
            link.ReferenceId,
            link.ExpiresAt);
    }

    /// <summary>Stops an active link being paid. A link cannot be deleted or edited; this is the only mutation.</summary>
    /// <param name="paymentLinkId">Beam's id for the link.</param>
    /// <param name="cancellationToken">Propagated to the HTTP call.</param>
    /// <exception cref="ArgumentException"><paramref name="paymentLinkId"/> is null or blank.</exception>
    /// <exception cref="PaymentApiException">Beam rejected the call.</exception>
    public Task DisablePaymentLinkAsync(string paymentLinkId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentLinkId);
        var httpClient = httpClientFactory.CreateClient(BeamPaymentGateway.HttpClientName);
        return BeamPaymentLinks.DisableAsync(httpClient, options.Value, paymentLinkId, cancellationToken);
    }

    /// <summary>Verifies a slip whose QR content the caller already scanned (<c>format=RAW</c>).</summary>
    /// <param name="rawQrContent">
    /// The QR code's content <b>as printed on the slip</b> — not the QR the shopper scanned to pay; those two
    /// codes differ.
    /// </param>
    /// <param name="cancellationToken">Propagated to the HTTP call.</param>
    /// <exception cref="PaymentApiException">
    /// <paramref name="rawQrContent"/> is blank, or Beam rejected the call — including
    /// <see cref="FailureKind.NotFound"/> when the slip matches no charge of this merchant's.
    /// </exception>
    public Task<BeamSlipVerification> VerifyQrSlipAsync(string rawQrContent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawQrContent))
        {
            throw new PaymentApiException(FailureKind.Validation, "slip_raw_required", 0);
        }

        var httpClient = httpClientFactory.CreateClient(BeamPaymentGateway.HttpClientName);
        return SendSlipVerificationAsync(httpClient, options.Value, () => BuildRawSlipContent(rawQrContent), cancellationToken);
    }

    /// <summary>Verifies a slip image, letting Beam read the QR (<c>format=IMAGE</c>; JPEG or PNG, at most 2 MB).</summary>
    /// <param name="image">The slip image. Read fully and buffered before the first attempt; safe to dispose after this returns.</param>
    /// <param name="fileName">
    /// The image's file name — only its extension is used, to tell Beam the image type. Must end in
    /// <c>.png</c>, <c>.jpg</c> or <c>.jpeg</c> (case-insensitive).
    /// </param>
    /// <param name="cancellationToken">Propagated to the HTTP call.</param>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> is null or blank.</exception>
    /// <exception cref="PaymentApiException">
    /// The image is larger than 2 MB or <paramref name="fileName"/>'s extension is unsupported, or Beam
    /// rejected the call — including <see cref="FailureKind.NotFound"/> when the slip matches no charge of
    /// this merchant's.
    /// </exception>
    public async Task<BeamSlipVerification> VerifyQrSlipAsync(
        Stream image, string fileName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (!HasSupportedImageExtension(fileName))
        {
            throw new PaymentApiException(FailureKind.Validation, "slip_image_type_unsupported", 0);
        }

        var imageBytes = await ReadWithSizeLimitAsync(image, cancellationToken).ConfigureAwait(false);
        var httpClient = httpClientFactory.CreateClient(BeamPaymentGateway.HttpClientName);
        return await SendSlipVerificationAsync(
            httpClient, options.Value, () => BuildImageSlipContent(imageBytes, fileName), cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateLinkRequest(BeamPaymentLinkRequest request) =>
        CreateChargeRequestValidator.Validate(new CreateChargeRequest
        {
            Amount = request.Amount,
            ReferenceId = request.ReferenceId,
            AllowedMethods = request.AllowedMethods,
            ExpiresAt = request.ExpiresAt,
        });

    private static BeamPaymentLinkStatus ToLinkStatus(string status) => status switch
    {
        "ACTIVE" => BeamPaymentLinkStatus.Active,
        "DISABLED" => BeamPaymentLinkStatus.Disabled,
        "EXPIRED" => BeamPaymentLinkStatus.Expired,
        "PAID" => BeamPaymentLinkStatus.Paid,
        "VOIDED" => BeamPaymentLinkStatus.Voided,
        "REFUNDED" => BeamPaymentLinkStatus.Refunded,
        _ => throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)HttpStatusCode.OK),
    };

    private static bool HasSupportedImageExtension(string fileName) =>
        fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <paramref name="image"/> into memory, throwing as soon as it exceeds the 2 MB limit rather than
    /// buffering it all first — a stream can only be read once, and the bytes must be buffered anyway so the
    /// retry wrapper can rebuild the multipart content on each attempt.
    /// </summary>
    private static async Task<byte[]> ReadWithSizeLimitAsync(Stream image, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await image.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxSlipImageBytes)
            {
                throw new PaymentApiException(FailureKind.Validation, "slip_image_too_large", 0);
            }
        }

        return buffer.ToArray();
    }

    private static MultipartFormDataContent BuildRawSlipContent(string rawQrContent) => new()
    {
        FormDataPart("RAW", "format"),
        FormDataPart(rawQrContent, "raw"),
    };

    private static MultipartFormDataContent BuildImageSlipContent(byte[] imageBytes, string fileName)
    {
        var imageContent = new ByteArrayContent(imageBytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        imageContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"image\"",
            FileName = $"\"{fileName}\"",
        };

        return new MultipartFormDataContent
        {
            FormDataPart("IMAGE", "format"),
            imageContent,
        };
    }

    /// <summary>
    /// A text form-data part with a properly quoted <c>name</c> — <see cref="MultipartFormDataContent"/>'s own
    /// <c>Add(content, name)</c> leaves <c>name</c> unquoted, which RFC 7578 requires as a quoted string.
    /// </summary>
    private static StringContent FormDataPart(string value, string name)
    {
        var content = new StringContent(value);
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
        return content;
    }

    private static async Task<BeamSlipVerification> SendSlipVerificationAsync(
        HttpClient httpClient, BeamOptions beamOptions, Func<HttpContent> contentFactory, CancellationToken cancellationToken)
    {
        var idempotencyKey = Guid.NewGuid().ToString("N");
        using var response = await BeamHttp.SendWithRetryAsync(
            httpClient,
            HttpMethod.Post,
            "/api/v1/charges/verify-qr-slip",
            beamOptions,
            contentFactory,
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

            if (!parsed ||
                root.ValueKind != JsonValueKind.Object ||
                !BeamMapping.TryGetNonEmptyString(root, "chargeId", out var chargeId) ||
                !BeamMapping.TryGetNonEmptyString(root, "verificationResult", out var verificationResult))
            {
                throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
            }

            BeamSlipVerificationResult result;
            switch (verificationResult)
            {
                case "UPDATED_TO_SUCCEEDED":
                    result = BeamSlipVerificationResult.UpdatedToSucceeded;
                    break;
                case "ALREADY_SUCCEEDED":
                    result = BeamSlipVerificationResult.AlreadySucceeded;
                    break;
                default:
                    throw new PaymentApiException(FailureKind.Unknown, "malformed_response", (int)response.StatusCode);
            }

            return new BeamSlipVerification(chargeId, result);
        }
    }
}
