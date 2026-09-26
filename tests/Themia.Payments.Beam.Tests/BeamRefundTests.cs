using System.Net;
using System.Text.Json;

using Themia.Payments;

using Xunit;

using static Themia.Payments.Beam.Tests.BeamTestHost;

namespace Themia.Payments.Beam.Tests;

public class BeamRefundTests
{
    [Fact]
    public async Task A_full_refund_posts_the_charge_id_and_returns_the_refund_id()
    {
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, """{ "refundId": "re_1" }""");
        var gateway = BuildWith(handler);

        var refund = await gateway.RefundAsync(new RefundRequest(new ChargeRef("ch_1", "order-1"), Amount: null, Reason: "duplicate", IdempotencyKey: "r-1"));

        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("ch_1", sent.RootElement.GetProperty("chargeId").GetString());
        Assert.False(sent.RootElement.TryGetProperty("amount", out _));
        Assert.Equal("re_1", refund.RefundId);
        Assert.Equal(PaymentStatus.Pending, refund.Status);
    }

    [Fact]
    public async Task A_partial_refund_of_a_qr_charge_is_refused_before_the_refund_call()
    {
        // The adapter reads the charge first, because only CARD charges can be refunded in part.
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, """
        { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 10000,
          "paymentMethod": { "paymentMethodType": "QR_PROMPT_PAY" } }
        """);
        var gateway = BuildWith(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.RefundAsync(
            new RefundRequest(new ChargeRef("ch_1", "order-1"), Money.Thb(5000), Reason: null, IdempotencyKey: null)));

        Assert.Equal("partial_refund_unsupported", ex.ProviderCode);
        Assert.Single(handler.Requests);                       // the GET only; no refund was attempted
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
    }

    [Fact]
    public async Task A_partial_refund_of_a_card_charge_sends_the_amount()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.OK, """
            { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 10000,
              "paymentMethod": { "paymentMethodType": "CARD" } }
            """)
            .Enqueue(HttpStatusCode.OK, """{ "refundId": "re_2" }""");
        var gateway = BuildWith(handler);

        await gateway.RefundAsync(new RefundRequest(new ChargeRef("ch_1", "order-1"), Money.Thb(5000), null, null));

        using var sent = JsonDocument.Parse(handler.Bodies[1]);
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
        var handler = new StubHandler().Enqueue(HttpStatusCode.OK, """
        { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 10000,
          "paymentMethod": { "paymentMethodType": "CARD" } }
        """);
        var gateway = BuildWith(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.RefundAsync(
            new RefundRequest(new ChargeRef("ch_1", "order-1"), Money.From(5000, "USD"), null, null)));

        Assert.Equal(FailureKind.Validation, ex.Kind);
        Assert.Equal("refund_currency_mismatch", ex.ProviderCode);
        Assert.Single(handler.Requests);                       // the GET only; no refund was attempted
    }
}
