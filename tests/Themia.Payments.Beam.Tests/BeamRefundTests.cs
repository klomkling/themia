using System.Net;
using System.Text.Json;

using Themia.Payments;

using Xunit;

using static Themia.Payments.Beam.Tests.BeamTestHost;

namespace Themia.Payments.Beam.Tests;

public class BeamRefundTests
{
    private const string CardCharge = """
        { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 10000,
          "paymentMethod": { "paymentMethodType": "CARD" } }
        """;

    private const string NotFound = """{ "error": { "errorCode": "NOT_FOUND_ERROR" } }""";

    private const string PaidLink = """
        { "paymentLinkId": "rGtqz6DafS", "status": "PAID",
          "order": { "netAmount": 10000, "currency": "THB", "referenceId": "order-1" } }
        """;

    private const string ChargesForLink = """
        { "data": [
            { "chargeId": "ch_9", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 10000,
              "source": "PAYMENT_LINK", "sourceId": "rGtqz6DafS" } ] }
        """;

    [Fact]
    public async Task A_full_refund_by_a_payment_link_id_posts_the_charge_that_paid_the_link()
    {
        // A lone Card or multi-method charge returns the LINK id as ChargeCreation.ChargeId; Beam refunds charges.
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound, NotFound)
            .Enqueue(HttpStatusCode.OK, PaidLink)
            .Enqueue(HttpStatusCode.OK, ChargesForLink)
            .Enqueue(HttpStatusCode.OK, """{ "refundId": "re_1" }""");
        var gateway = BuildWith(handler);

        await gateway.RefundAsync(new RefundRequest(new ChargeRef("rGtqz6DafS", "order-1"), null, null, null));

        Assert.Equal("/api/v1/refunds", handler.Requests[^1].RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(handler.Bodies[^1]);
        Assert.Equal("ch_9", sent.RootElement.GetProperty("chargeId").GetString());
    }

    [Fact]
    public async Task A_partial_refund_by_a_payment_link_id_posts_the_charge_that_paid_the_link()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound, NotFound)
            .Enqueue(HttpStatusCode.OK, PaidLink)
            .Enqueue(HttpStatusCode.OK, ChargesForLink)
            .Enqueue(HttpStatusCode.OK, CardCharge.Replace("ch_1", "ch_9", StringComparison.Ordinal))
            .Enqueue(HttpStatusCode.OK, """{ "refundId": "re_2" }""");
        var gateway = BuildWith(handler);

        await gateway.RefundAsync(new RefundRequest(new ChargeRef("rGtqz6DafS", "order-1"), Money.Thb(5000), null, null));

        Assert.Equal("/api/v1/charges/ch_9", handler.Requests[3].RequestUri!.AbsolutePath);
        using var sent = JsonDocument.Parse(handler.Bodies[^1]);
        Assert.Equal("ch_9", sent.RootElement.GetProperty("chargeId").GetString());
        Assert.Equal(5000, sent.RootElement.GetProperty("amount").GetInt64());
    }

    [Fact]
    public async Task A_refund_by_an_unpaid_payment_link_id_is_not_found_and_never_posted()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound, NotFound)
            .Enqueue(HttpStatusCode.OK, PaidLink.Replace("PAID", "ACTIVE", StringComparison.Ordinal));
        var gateway = BuildWith(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.RefundAsync(
            new RefundRequest(new ChargeRef("rGtqz6DafS", "order-1"), null, null, null)));

        Assert.Equal(FailureKind.NotFound, ex.Kind);
        Assert.Equal("no_charge_for_link", ex.ProviderCode);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task A_full_refund_posts_the_charge_id_and_returns_the_refund_id()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.OK, CardCharge)
            .Enqueue(HttpStatusCode.OK, """{ "refundId": "re_1" }""");
        var gateway = BuildWith(handler);

        var refund = await gateway.RefundAsync(new RefundRequest(new ChargeRef("ch_1", "order-1"), Amount: null, Reason: "duplicate", IdempotencyKey: "r-1"));

        using var sent = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("ch_1", sent.RootElement.GetProperty("chargeId").GetString());
        Assert.False(sent.RootElement.TryGetProperty("amount", out _));
        Assert.Equal("re_1", refund.RefundId);
        Assert.Equal(PaymentStatus.Pending, refund.Status);
    }

    [Fact]
    public async Task A_partial_refund_of_a_qr_charge_is_refused_before_the_refund_call()
    {
        // The adapter reads the charge first, because only CARD charges can be refunded in part.
        const string QrCharge = """
        { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 10000,
          "paymentMethod": { "paymentMethodType": "QR_PROMPT_PAY" } }
        """;
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, QrCharge).Enqueue(HttpStatusCode.OK, QrCharge);
        var gateway = BuildWith(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.RefundAsync(
            new RefundRequest(new ChargeRef("ch_1", "order-1"), Money.Thb(5000), Reason: null, IdempotencyKey: null)));

        Assert.Equal("partial_refund_unsupported", ex.ProviderCode);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method)); // no refund was attempted
    }

    [Fact]
    public async Task A_partial_refund_of_a_card_charge_sends_the_amount()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.OK, CardCharge)
            .Enqueue(HttpStatusCode.OK, CardCharge)
            .Enqueue(HttpStatusCode.OK, """{ "refundId": "re_2" }""");
        var gateway = BuildWith(handler);

        await gateway.RefundAsync(new RefundRequest(new ChargeRef("ch_1", "order-1"), Money.Thb(5000), null, null));

        using var sent = JsonDocument.Parse(handler.Bodies[^1]);
        Assert.Equal(5000, sent.RootElement.GetProperty("amount").GetInt64());
    }

    [Fact]
    public async Task A_refund_without_a_provider_charge_id_resolves_it_by_reference_first()
    {
        // No ProviderChargeId: the adapter resolves one through the same by-reference lookup GetChargeAsync
        // uses, preferring the charge that actually succeeded.
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.OK, """
            { "data": [ { "chargeId": "ch_5", "referenceId": "order-9", "status": "SUCCEEDED", "currency": "THB", "amount": 500 } ] }
            """)
            .Enqueue(HttpStatusCode.Created, """{ "refundId": "re_9" }""");
        var gateway = BuildWith(handler);

        var refund = await gateway.RefundAsync(new RefundRequest(new ChargeRef(null, "order-9"), null, null, null));

        Assert.Equal("/api/v1/charges", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("referenceId=order-9", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        using var sent = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("ch_5", sent.RootElement.GetProperty("chargeId").GetString());
        Assert.Equal("re_9", refund.RefundId);
    }

    [Fact]
    public async Task A_refund_with_no_charge_for_the_reference_is_reported_as_not_found()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, """{ "data": [] }""");
        var gateway = BuildWith(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.RefundAsync(
            new RefundRequest(new ChargeRef(null, "order-404"), null, null, null)));

        Assert.Equal(FailureKind.NotFound, ex.Kind);
        Assert.Equal("no_charge_for_reference", ex.ProviderCode);
    }

    [Fact]
    public async Task A_partial_refund_whose_currency_differs_from_the_charges_is_refused()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, CardCharge).Enqueue(HttpStatusCode.OK, CardCharge);
        var gateway = BuildWith(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.RefundAsync(
            new RefundRequest(new ChargeRef("ch_1", "order-1"), Money.From(5000, "USD"), null, null)));

        Assert.Equal(FailureKind.Validation, ex.Kind);
        Assert.Equal("refund_currency_mismatch", ex.ProviderCode);
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method)); // no refund was attempted
    }
}
