using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Themia.Payments;
using Themia.Payments.Beam;

using Xunit;

using static Themia.Payments.Beam.Tests.BeamTestHost;

namespace Themia.Payments.Beam.Tests;

public class BeamPaymentClientTests
{
    [Fact]
    public async Task Creating_a_link_posts_the_order_and_returns_an_active_link()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.Created, PaymentLinkResponse);
        var client = BuildClient(handler);

        var link = await client.CreatePaymentLinkAsync(new BeamPaymentLinkRequest
        {
            Amount = Money.Thb(250000),
            ReferenceId = "order-3",
            AllowedMethods = [PaymentMethod.QrPromptPay, PaymentMethod.Card],
            CollectPhoneNumber = true,
            CollectDeliveryAddress = true,
            CancelUrl = new Uri("https://shop.example/cancel"),
        });

        Assert.Equal("/api/v1/payment-links", handler.Requests[0].RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        var order = sent.RootElement.GetProperty("order");
        Assert.Equal(250000, order.GetProperty("netAmount").GetInt64());
        Assert.Equal("order-3", order.GetProperty("referenceId").GetString());
        Assert.True(sent.RootElement.GetProperty("collectPhoneNumber").GetBoolean());
        Assert.True(sent.RootElement.GetProperty("collectDeliveryAddress").GetBoolean());
        Assert.Equal("https://shop.example/cancel", sent.RootElement.GetProperty("cancelUrl").GetString());

        var settings = sent.RootElement.GetProperty("linkSettings");
        Assert.True(settings.GetProperty("qrPromptPay").GetProperty("isEnabled").GetBoolean());
        Assert.True(settings.GetProperty("card").GetProperty("isEnabled").GetBoolean());

        Assert.Equal("rGtqz6DafS", link.Id);
        Assert.Equal(new Uri("https://playground-pay.beamcheckout.com/m/rGtqz6DafS"), link.Url);
        Assert.Equal(BeamPaymentLinkStatus.Active, link.Status);
        Assert.Equal(Money.Thb(250000), link.Amount);
        Assert.Equal("order-3", link.ReferenceId);
    }

    [Fact]
    public async Task Creating_a_link_honours_the_method_policy()
    {
        // A policy forbidding Card at this amount must refuse locally, before any HTTP call — same guard the
        // gateway applies to CreateChargeAsync (Task 9's PaymentMethodGate), so this client cannot be used to
        // route around an adopter's fee policy.
        var handler = new StubHandler();
        var gate = new PaymentMethodGate(Options.Create(new ThemiaPaymentsOptions
        {
            MethodPolicy = new PaymentMethodPolicy
            {
                Bands = [new PaymentMethodBand(100000, [PaymentMethod.QrPromptPay])],
                Above = [PaymentMethod.QrPromptPay],
            },
        }));
        var client = BuildClient(handler, gate);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.CreatePaymentLinkAsync(new BeamPaymentLinkRequest
        {
            Amount = Money.Thb(50000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.Card],
        }));

        Assert.Equal("method_not_allowed_for_amount", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Getting_a_link_maps_paid()
    {
        var (_, handler) = Build(PaymentLinkGetResponse.Replace("\"ACTIVE\"", "\"PAID\"", StringComparison.Ordinal));
        var client = BuildClient(handler);

        var link = await client.GetPaymentLinkAsync("rGtqz6DafS");

        Assert.Equal(BeamPaymentLinkStatus.Paid, link.Status);
        Assert.Equal("rGtqz6DafS", link.Id);
        Assert.Equal(new Uri("https://playground-pay.beamcheckout.com/m/rGtqz6DafS"), link.Url);
        Assert.Equal(Money.Thb(250000), link.Amount);
        Assert.Equal("order-3", link.ReferenceId);
    }

    [Fact]
    public async Task Getting_a_link_with_an_unrecognized_status_becomes_a_typed_exception()
    {
        var (_, handler) = Build("""
        { "paymentLinkId": "L", "url": "https://playground-pay.beamcheckout.com/m/L", "status": "SOMETHING_NEW",
          "order": { "netAmount": 100, "currency": "THB", "referenceId": "o" } }
        """);
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.GetPaymentLinkAsync("L"));

        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task Getting_a_link_with_no_url_is_a_malformed_response()
    {
        var (_, handler) = Build("""
        { "paymentLinkId": "L", "status": "ACTIVE",
          "order": { "netAmount": 100, "currency": "THB", "referenceId": "o" } }
        """);
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.GetPaymentLinkAsync("L"));

        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task Disabling_a_link_patches_and_accepts_202()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.Accepted, "{}");
        var client = BuildClient(handler);

        await client.DisablePaymentLinkAsync("rGtqz6DafS");

        Assert.Equal(HttpMethod.Patch, handler.Requests[0].Method);
        Assert.Equal("/api/v1/payment-links/rGtqz6DafS/disable", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Slip_verification_sends_multipart_with_the_raw_qr_content()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK,
            """{ "chargeId": "ch_1", "verificationResult": "UPDATED_TO_SUCCEEDED" }""");
        var client = BuildClient(handler);

        var result = await client.VerifyQrSlipAsync("0041000600000101030040220015077082818AQR086795102TH9104FF93");

        Assert.Equal("/api/v1/charges/verify-qr-slip", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("form-data; name=\"format\"", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains("RAW", handler.Bodies[0], StringComparison.Ordinal);
        Assert.Equal(BeamSlipVerificationResult.UpdatedToSucceeded, result.Result);
        Assert.Equal("ch_1", result.ChargeId);
    }

    [Fact]
    public async Task Slip_verification_maps_already_succeeded()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK,
            """{ "chargeId": "ch_2", "verificationResult": "ALREADY_SUCCEEDED" }""");
        var client = BuildClient(handler);

        var result = await client.VerifyQrSlipAsync("0041");

        Assert.Equal(BeamSlipVerificationResult.AlreadySucceeded, result.Result);
    }

    [Fact]
    public async Task A_slip_matching_no_charge_is_a_not_found_failure_not_an_unverified_result()
    {
        // Beam's verificationResult never reports failure; the HTTP status is the answer.
        var handler = new StubHandler().Enqueue(HttpStatusCode.NotFound,
            """{ "code": 404, "message": "not found", "error": { "errorCode": "NOT_FOUND_ERROR" } }""");
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.VerifyQrSlipAsync("0041"));

        Assert.Equal(FailureKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task A_blank_raw_qr_content_is_refused_locally()
    {
        var handler = new StubHandler();
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.VerifyQrSlipAsync("   "));

        Assert.Equal("slip_raw_required", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task An_oversized_slip_image_is_refused_before_any_request()
    {
        var handler = new StubHandler();
        var client = BuildClient(handler);
        using var oversized = new MemoryStream(new byte[(2 * 1024 * 1024) + 1]);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.VerifyQrSlipAsync(oversized, "slip.png"));

        Assert.Equal("slip_image_too_large", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_slip_image_with_an_unsupported_extension_is_refused_before_any_request()
    {
        var handler = new StubHandler();
        var client = BuildClient(handler);
        using var image = new MemoryStream([1, 2, 3]);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.VerifyQrSlipAsync(image, "slip.gif"));

        Assert.Equal("slip_image_type_unsupported", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_slip_image_filename_containing_a_double_quote_is_refused_before_any_request()
    {
        // Unescaped, this would break out of the hand-built Content-Disposition's quoted fileName.
        var handler = new StubHandler();
        var client = BuildClient(handler);
        using var image = new MemoryStream([1, 2, 3]);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.VerifyQrSlipAsync(image, "sl\"ip.png"));

        Assert.Equal("slip_image_filename_invalid", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_slip_image_filename_containing_crlf_is_refused_before_any_request()
    {
        // Unescaped, this would inject extra header lines into the multipart part.
        var handler = new StubHandler();
        var client = BuildClient(handler);
        using var image = new MemoryStream([1, 2, 3]);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.VerifyQrSlipAsync(image, "sl\r\nip.png"));

        Assert.Equal("slip_image_filename_invalid", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_slip_image_retry_after_a_503_resends_the_same_bytes()
    {
        var imageBytes = Encoding.UTF8.GetBytes("not a real png, just some bytes");
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue(HttpStatusCode.OK, """{ "chargeId": "ch_3", "verificationResult": "UPDATED_TO_SUCCEEDED" }""");
        var client = BuildClient(handler);
        using var image = new MemoryStream(imageBytes);

        var result = await client.VerifyQrSlipAsync(image, "slip.jpg");

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(BeamSlipVerificationResult.UpdatedToSucceeded, result.Result);
        Assert.Contains(Encoding.UTF8.GetString(imageBytes), handler.Bodies[0], StringComparison.Ordinal);
        Assert.Contains(Encoding.UTF8.GetString(imageBytes), handler.Bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payment_link_with_an_invalid_idempotency_key_is_refused_before_the_call()
    {
        var handler = new StubHandler();
        var client = BeamTestHost.BuildClient(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => client.CreatePaymentLinkAsync(new BeamPaymentLinkRequest
        {
            Amount = Money.Thb(10000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.Card],
            IdempotencyKey = "key\r\nX-Injected: 1",
        }));

        Assert.Equal("idempotency_key_invalid", ex.ProviderCode);
        Assert.Empty(handler.Requests);
    }
}
