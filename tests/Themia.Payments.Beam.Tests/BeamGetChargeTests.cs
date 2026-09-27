using System.Net;

using Themia.Payments;

using Xunit;

using static Themia.Payments.Beam.Tests.BeamTestHost;

namespace Themia.Payments.Beam.Tests;

public class BeamGetChargeTests
{
    [Fact]
    public async Task A_failed_charge_carries_the_reason_and_the_raw_code()
    {
        var (gateway, _) = Build("""
        { "chargeId": "ch_1", "referenceId": "order-1", "status": "FAILED", "currency": "THB", "amount": 199,
          "failureCode": "CH_INSUFFICIENT_FUNDS", "transactionTime": "2026-09-22T10:00:00Z" }
        """);

        var charge = await gateway.GetChargeAsync(new ChargeRef("ch_1", "order-1"));

        Assert.Equal(PaymentStatus.Failed, charge.Status);
        Assert.Equal(FailureReason.InsufficientFunds, charge.Failure!.Reason);
        Assert.Equal("CH_INSUFFICIENT_FUNDS", charge.Failure.ProviderCode);
        Assert.Equal(Money.Thb(199), charge.Amount);
    }

    [Fact]
    public async Task Without_a_provider_id_the_charge_is_found_by_reference_preferring_the_one_that_succeeded()
    {
        // GET /api/v1/charges?referenceId= lists most recent first. A link can carry several attempts, so a later
        // failed attempt must not hide the one that actually paid.
        var (gateway, handler) = Build("""
        { "data": [
            { "chargeId": "ch_3", "referenceId": "order-1", "status": "FAILED", "currency": "THB", "amount": 199, "failureCode": "CH_PROCESSING_FAILED" },
            { "chargeId": "ch_2", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 199 } ] }
        """);

        var charge = await gateway.GetChargeAsync(new ChargeRef(ProviderChargeId: null, ReferenceId: "order-1"));

        Assert.Equal("/api/v1/charges", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Contains("referenceId=order-1", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal("ch_2", charge.ChargeId);
        Assert.Equal(PaymentStatus.Succeeded, charge.Status);
    }

    [Fact]
    public async Task A_whitespace_provider_id_counts_as_unset_and_the_charge_is_found_by_reference()
    {
        var (gateway, handler) = Build("""
        { "data": [ { "chargeId": "ch_2", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 199 } ] }
        """);

        var charge = await gateway.GetChargeAsync(new ChargeRef(ProviderChargeId: "  ", ReferenceId: "order-1"));

        Assert.Equal("/api/v1/charges", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("ch_2", charge.ChargeId);
    }

    [Fact]
    public async Task A_payment_link_id_is_followed_to_the_charge_that_paid_it()
    {
        // ChargeCreation for a multi-method request carries the link id (Task 7). Reading it back must work.
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound, """{ "error": { "errorCode": "NOT_FOUND_ERROR" } }""")          // not a charge id
            .Enqueue(HttpStatusCode.OK, """
            { "paymentLinkId": "rGtqz6DafS", "status": "PAID",
              "order": { "netAmount": 199, "currency": "THB", "referenceId": "order-1" } }
            """)
            .Enqueue(HttpStatusCode.OK, """
            { "data": [
                { "chargeId": "ch_9", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 199,
                  "source": "PAYMENT_LINK", "sourceId": "rGtqz6DafS" } ] }
            """);
        var gateway = BuildWith(handler);

        var charge = await gateway.GetChargeAsync(new ChargeRef("rGtqz6DafS", "order-1"));

        Assert.Equal("/api/v1/payment-links/rGtqz6DafS", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Contains("sourceId=rGtqz6DafS", handler.Requests[2].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal("ch_9", charge.ChargeId);
        Assert.Equal(PaymentStatus.Succeeded, charge.Status);
    }

    [Fact]
    public async Task An_unpaid_active_link_reads_as_pending_with_the_links_amount()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound, """{ "error": { "errorCode": "NOT_FOUND_ERROR" } }""")
            .Enqueue(HttpStatusCode.OK, """
            { "paymentLinkId": "rGtqz6DafS", "status": "ACTIVE",
              "order": { "netAmount": 250000, "currency": "THB", "referenceId": "order-3" } }
            """);
        var gateway = BuildWith(handler);

        var charge = await gateway.GetChargeAsync(new ChargeRef("rGtqz6DafS", "order-3"));

        Assert.Equal(PaymentStatus.Pending, charge.Status);
        Assert.Equal(Money.Thb(250000), charge.Amount);
        Assert.Equal("rGtqz6DafS", charge.ChargeId);
    }

    [Theory]
    [InlineData("EXPIRED", FailureReason.Expired)]
    [InlineData("DISABLED", FailureReason.Canceled)]
    public async Task A_link_that_can_no_longer_be_paid_reads_as_failed(string linkStatus, FailureReason reason)
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound, """{ "error": { "errorCode": "NOT_FOUND_ERROR" } }""")
            .Enqueue(HttpStatusCode.OK, $$"""
            { "paymentLinkId": "L", "status": "{{linkStatus}}",
              "order": { "netAmount": 100, "currency": "THB", "referenceId": "o" } }
            """);
        var gateway = BuildWith(handler);

        var charge = await gateway.GetChargeAsync(new ChargeRef("L", "o"));

        Assert.Equal(PaymentStatus.Failed, charge.Status);
        Assert.Equal(reason, charge.Failure!.Reason);
        Assert.Equal(linkStatus, charge.Failure.ProviderCode);
    }

    [Fact]
    public async Task A_non_integral_amount_becomes_a_typed_exception()
    {
        // 199.5 is a valid JSON number but not a valid minor-units amount — must not raise a raw FormatException.
        var (gateway, _) = Build("""
        { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 199.5 }
        """);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.GetChargeAsync(new ChargeRef("ch_1", "order-1")));

        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task An_unparsable_transaction_time_on_a_succeeded_charge_becomes_a_typed_exception()
    {
        var (gateway, _) = Build("""
        { "chargeId": "ch_1", "referenceId": "order-1", "status": "SUCCEEDED", "currency": "THB", "amount": 199,
          "transactionTime": "not-a-date" }
        """);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.GetChargeAsync(new ChargeRef("ch_1", "order-1")));

        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal("malformed_response", ex.ProviderCode);
    }

    [Fact]
    public async Task A_non_integral_link_net_amount_becomes_a_typed_exception()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.NotFound, """{ "error": { "errorCode": "NOT_FOUND_ERROR" } }""")
            .Enqueue(HttpStatusCode.OK, """
            { "paymentLinkId": "L", "status": "ACTIVE",
              "order": { "netAmount": 1.5, "currency": "THB", "referenceId": "o" } }
            """);
        var gateway = BuildWith(handler);

        var ex = await Assert.ThrowsAsync<PaymentApiException>(() => gateway.GetChargeAsync(new ChargeRef("L", "o")));

        Assert.Equal(FailureKind.Unknown, ex.Kind);
        Assert.Equal("malformed_response", ex.ProviderCode);
    }
}
