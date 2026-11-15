using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Themia.Conversations.AspNetCore.Tests;

public sealed class RateLimitTests
{
    private static readonly Guid ConversationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private static Task<TestApp> StartAsync(FakeTime time, Action<ConversationHttpOptions> configure) =>
        TestApp.StartAsync(configure, services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time)));

    private static Task<HttpResponseMessage> Exchange(TestApp app, string? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/guest/exchange")
        {
            Content = new StringContent("{\"tenantId\":\"tenant-a\",\"token\":\"nope\"}", System.Text.Encoding.UTF8, "application/json"),
        };
        if (client is not null)
        {
            request.Headers.Add("X-Test-Client", client);
        }

        return app.SendAsync(request);
    }

    [Fact]
    public async Task Exchange_ShouldReturn429WithRetryAfter_WhenOneClientExceedsTheHourlyLimit_AndRecoverAfterTheWindow()
    {
        var time = new FakeTime();
        await using var app = await StartAsync(time, o => o.ExchangesPerHour = 2);

        await Exchange(app);
        await Exchange(app);
        var limited = await Exchange(app);
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        var after = await Exchange(app);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds > 0);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Exchange_ShouldGiveEachHostClientKeyItsOwnBucket_ForOneRemoteAddress()
    {
        await using var app = await StartAsync(new FakeTime(), o =>
        {
            o.ExchangesPerHour = 1;
            o.ClientKey = c => c.Request.Headers["X-Test-Client"].ToString();
        });

        var first = await Exchange(app, "client-1");
        var second = await Exchange(app, "client-2");
        var firstAgain = await Exchange(app, "client-1");

        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, firstAgain.StatusCode);
    }

    [Fact]
    public async Task UnprovenGuest_ShouldBeLimitedByClient_WhenEveryRequestCarriesADifferentRandomKey()
    {
        await using var app = await StartAsync(new FakeTime(), o => o.UnprovenGuestRequestsPerMinute = 3);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 5; i++)
        {
            var response = await app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationId, "messages"), cookie: GateTests.CookieFor(ConversationId, "tenant-a", GateTests.NewKey()));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests }, statuses);
        Assert.Equal(3, app.Service.Calls.Count);
    }

    [Fact]
    public async Task Post_ShouldReturn429WithoutReadingTheBody_WhenAMemberExceedsTheirPostLimit_AndNotLimitAnotherMember()
    {
        await using var app = await StartAsync(new FakeTime(), o => o.PostsPerMinute = 2);
        var body = new { clientMessageId = "m-1", body = "hi" };
        var path = TestApp.Path(ConversationId, "messages");

        await app.SendAsync(HttpMethod.Post, path, body, member: "tenant-a:alice");
        await app.SendAsync(HttpMethod.Post, path, body, member: "tenant-a:alice");
        var readsBefore = app.BodyReads.Count;
        var limited = await app.SendAsync(HttpMethod.Post, path, body, member: "tenant-a:alice");
        var readsAfter = app.BodyReads.Count;
        var other = await app.SendAsync(HttpMethod.Post, path, body, member: "tenant-a:bob");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(readsBefore, readsAfter);
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
    }

    [Fact]
    public async Task Startup_ShouldWarnAboutTheSharedBucket_OnlyWhenClientKeyIsTheDefault()
    {
        await using var defaulted = await TestApp.StartAsync();
        await using var configured = await TestApp.StartAsync(o => o.ClientKey = _ => "x");

        Assert.Contains(defaulted.Logs.Lines, l => l.Contains("ClientKey", StringComparison.Ordinal));
        Assert.DoesNotContain(configured.Logs.Lines, l => l.Contains("ClientKey", StringComparison.Ordinal));
    }
}
