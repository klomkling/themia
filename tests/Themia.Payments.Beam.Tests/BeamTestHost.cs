using System.Net;
using Microsoft.Extensions.Options;
using Themia.Payments;
using Themia.Payments.Beam;

namespace Themia.Payments.Beam.Tests;

/// <summary>
/// Shared gateway-building helpers for the Beam adapter's tests. Tasks 7, 8, 9 and 11 all need a gateway
/// wired to a <see cref="StubHandler"/>, so this lives once instead of copied per test class.
/// </summary>
internal static class BeamTestHost
{
    /// <summary>A direct-charge response with an <c>ENCODED_IMAGE</c> action.</summary>
    public const string EncodedImageResponse = """
    {
      "actionRequired": "ENCODED_IMAGE",
      "chargeId": "ch_2xTsz7Qit55pahSvKfJG3UMkpFQ",
      "encodedImage": {
        "expiry": "2025-08-24T14:15:22Z",
        "imageBase64Encoded": "aGVsbG8=",
        "rawData": "00020101"
      },
      "paymentMethodType": "QR_PROMPT_PAY"
    }
    """;

    /// <summary>
    /// A payment-link CREATE response (Beam's <c>CreatePaymentLinkResponse</c>, HTTP 201): <c>id</c> and
    /// <c>url</c> only. Do not confuse with <see cref="PaymentLinkGetResponse"/> — the GET shape carries
    /// <c>paymentLinkId</c> instead of <c>id</c>, plus <c>status</c> and <c>order</c>.
    /// </summary>
    public const string PaymentLinkResponse = """
    { "id": "rGtqz6DafS", "url": "https://playground-pay.beamcheckout.com/m/rGtqz6DafS" }
    """;

    /// <summary>
    /// A payment-link GET response: <c>paymentLinkId</c>, <c>url</c>, <c>status</c> and <c>order</c>. Not
    /// used by any test yet — added for Task 8's GetChargeAsync-follows-a-link-id work.
    /// </summary>
    public const string PaymentLinkGetResponse = """
    { "paymentLinkId": "rGtqz6DafS", "url": "https://playground-pay.beamcheckout.com/m/rGtqz6DafS",
      "status": "ACTIVE", "order": { "netAmount": 250000, "currency": "THB", "referenceId": "order-3" } }
    """;

    /// <summary>Builds a gateway backed by a fresh handler queued with one response.</summary>
    public static (BeamPaymentGateway Gateway, StubHandler Handler) Build(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler().Enqueue(status, body);
        return (BuildWith(handler), handler);
    }

    /// <summary>Builds a gateway backed by a caller-supplied handler, for tests that queue several responses.</summary>
    public static BeamPaymentGateway BuildWith(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = BeamOptions.BaseAddressFor(BeamEnvironment.Playground) };
        var options = Options.Create(new BeamOptions { MerchantId = "m", ApiKey = "k" });
        var gate = new PaymentMethodGate(Options.Create(new ThemiaPaymentsOptions()));
        return new BeamPaymentGateway(new StubClientFactory(client), options, gate);
    }

    /// <summary>
    /// Builds a <see cref="BeamPaymentClient"/> backed by a caller-supplied handler (Task 11). Takes an
    /// optional <paramref name="gate"/> so a test can install a method policy that would otherwise leave the
    /// default no-op gate letting everything through.
    /// </summary>
    public static BeamPaymentClient BuildClient(StubHandler handler, PaymentMethodGate? gate = null)
    {
        var client = new HttpClient(handler) { BaseAddress = BeamOptions.BaseAddressFor(BeamEnvironment.Playground) };
        var options = Options.Create(new BeamOptions { MerchantId = "m", ApiKey = "k" });
        return new BeamPaymentClient(new StubClientFactory(client), options, gate ?? new PaymentMethodGate(Options.Create(new ThemiaPaymentsOptions())));
    }

    private sealed class StubClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
