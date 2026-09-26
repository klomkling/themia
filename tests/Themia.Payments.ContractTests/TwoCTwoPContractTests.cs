using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Themia.Payments.Beam.Tests;
using Themia.Payments.TwoCTwoP;
using Themia.Payments.TwoCTwoP.Internal;

namespace Themia.Payments.ContractTests;

/// <summary>Runs <see cref="PaymentGatewayContract"/> against the 2C2P adapter.</summary>
public sealed class TwoCTwoPContractTests : PaymentGatewayContract
{
    private const string Secret = "test-secret";

    protected override IPaymentGateway CreateGateway(StubHandler handler, PaymentMethodPolicy? policy = null)
    {
        var client = new HttpClient(handler) { BaseAddress = TwoCTwoPOptions.BaseAddressFor(TwoCTwoPEnvironment.Sandbox) };
        var options = Options.Create(new TwoCTwoPOptions { MerchantId = "JT01", SecretKey = Secret });
        var gate = new PaymentMethodGate(Options.Create(new ThemiaPaymentsOptions { MethodPolicy = policy }));
        return new TwoCTwoPPaymentGateway(new StubClientFactory(client), options, gate);
    }

    /// <summary>A single-method QR PromptPay request signs a <c>paymentToken</c> JWT and gets back a hosted-page redirect.</summary>
    protected override void ScriptChargeCreated(StubHandler handler) =>
        handler.Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["webPaymentUrl"] = "https://sandbox-pgw-ui.2c2p.com/payment/4.3/#/token/abc",
            ["respCode"] = "0000",
            ["respDesc"] = "Success",
        }));

    /// <summary>2C2P reads a charge by the app's own reference (<c>invoiceNo</c>), never by a provider charge id
    /// it never minted — <c>GetChargeAsync</c> prefers <see cref="ChargeRef.ReferenceId"/> for exactly this reason.</summary>
    protected override void ScriptChargeSucceeded(StubHandler handler) =>
        handler.Enqueue(HttpStatusCode.OK, ResponseEnvelope(new Dictionary<string, object?>
        {
            ["invoiceNo"] = "order-1",
            ["amount"] = 1000.00m,
            ["currencyCode"] = "THB",
            ["respCode"] = "0000",
            ["respDesc"] = "Success",
            ["transactionDateTime"] = "20260927153000",
        }));

    private static string ResponseEnvelope(Dictionary<string, object?> claims) =>
        JsonSerializer.Serialize(new { payload = JwtHs256.Encode(claims, Secret) });

    private sealed class StubClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
