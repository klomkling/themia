using System.Net;

using Themia.Payments;

using Xunit;

using static Themia.Payments.Beam.Tests.BeamTestHost;

namespace Themia.Payments.Beam.Tests;

public class BeamRetryTests
{
    [Fact]
    public async Task A_retry_after_a_500_reuses_the_same_idempotency_key()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.InternalServerError, "{}")
            .Enqueue(HttpStatusCode.OK, EncodedImageResponse);
        var gateway = BuildWith(handler);

        await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(10000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.QrPromptPay],
            IdempotencyKey = "key-1",
        });

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
            Assert.Equal("key-1", r.Headers.GetValues("x-beam-idempotency-key").Single()));
    }

    [Fact]
    public async Task A_generated_key_is_also_stable_across_attempts()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue(HttpStatusCode.OK, EncodedImageResponse);
        var gateway = BuildWith(handler);

        await gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(10000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.QrPromptPay],
        });

        var keys = handler.Requests.Select(r => r.Headers.GetValues("x-beam-idempotency-key").Single()).Distinct();
        Assert.Single(keys);
    }

    [Fact]
    public async Task A_400_is_not_retried()
    {
        var handler = new StubHandler()
            .Enqueue(HttpStatusCode.BadRequest, """{ "error": { "errorCode": "API_VALIDATION_ERROR" } }""");
        var gateway = BuildWith(handler);

        await Assert.ThrowsAsync<PaymentApiException>(() => gateway.CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(10000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.QrPromptPay],
        }));

        Assert.Single(handler.Requests);
    }
}
