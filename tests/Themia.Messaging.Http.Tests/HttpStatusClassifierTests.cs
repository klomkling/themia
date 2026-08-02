using Themia.Messaging.Outbox;

using Xunit;

namespace Themia.Messaging.Http.Tests;

public class HttpStatusClassifierTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(204)]
    public void Classify_ShouldBeDelivered_For2xx(int status)
        => Assert.Equal(DispatchOutcome.Delivered, HttpStatusClassifier.Classify(status));

    // 408 is the scheme's stale-timestamp status. Classifying it permanent would dead-letter every
    // message a clock-drifted sender produces — the exact failure themia-hmac-v1 exists to prevent.
    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void Classify_ShouldBeTransient_ForRetryableStatuses(int status)
        => Assert.Equal(DispatchOutcome.Transient, HttpStatusClassifier.Classify(status));

    // Retrying an identical signature fails identically, so auth failures must surface at once.
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(422)]
    public void Classify_ShouldBePermanent_ForClientErrors(int status)
        => Assert.Equal(DispatchOutcome.Permanent, HttpStatusClassifier.Classify(status));

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    public void Classify_ShouldBePermanent_ForRedirects(int status)
        => Assert.Equal(DispatchOutcome.Permanent, HttpStatusClassifier.Classify(status));
}
