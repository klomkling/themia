using System.Net;

using Microsoft.Extensions.Options;

using Themia.Payments.Beam;
using Themia.Payments.Beam.Tests;

namespace Themia.Payments.ContractTests;

/// <summary>Runs <see cref="PaymentGatewayContract"/> against the Beam adapter.</summary>
public sealed class BeamContractTests : PaymentGatewayContract
{
    /// <summary>A direct QR PromptPay charge response (the body from Task 7's <c>BeamTestHost.EncodedImageResponse</c>).</summary>
    private const string EncodedImageResponse = """
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

    /// <summary>A succeeded direct charge, read back by its provider id.</summary>
    private const string SucceededChargeResponse = """
    { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 100000 }
    """;

    protected override IPaymentGateway CreateGateway(StubHandler handler, PaymentMethodPolicy? policy = null)
    {
        var client = new HttpClient(handler) { BaseAddress = BeamOptions.BaseAddressFor(BeamEnvironment.Playground) };
        var options = Options.Create(new BeamOptions { MerchantId = "m", ApiKey = "k" });
        var gate = new PaymentMethodGate(Options.Create(new ThemiaPaymentsOptions { MethodPolicy = policy }));
        return new BeamPaymentGateway(new StubClientFactory(client), options, gate);
    }

    /// <summary>A single-method QR PromptPay request goes straight to a direct charge.</summary>
    protected override void ScriptChargeCreated(StubHandler handler) =>
        handler.Enqueue(HttpStatusCode.OK, EncodedImageResponse);

    /// <summary>Beam reads a charge by its own provider id.</summary>
    protected override void ScriptChargeSucceeded(StubHandler handler) =>
        handler.Enqueue(HttpStatusCode.OK, SucceededChargeResponse);

    private sealed class StubClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
