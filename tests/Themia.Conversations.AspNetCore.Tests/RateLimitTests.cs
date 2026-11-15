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
        app.Service.Page = new MessagePage(ListOutcome.Refused, []);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 5; i++)
        {
            var response = await app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationId, "messages"), cookie: GateTests.CookieFor(ConversationId, "tenant-a", GateTests.NewKey()));
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(new[] { HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests }, statuses);
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

    [Fact]
    public void TryAcquire_ShouldStayBoundedAndNeverLockOutNewKeys_WhenOneScopeIsFlooded()
    {
        var limiter = new Internal.RequestRateLimiter(new FakeTime());
        for (var i = 0; i < Internal.RequestRateLimiter.MaxEntries + 500; i++)
        {
            Assert.True(limiter.TryAcquire("flooded", "k" + i, 5, TimeSpan.FromMinutes(1), out _));
        }

        Assert.True(limiter.TryAcquire("post", "legit", 1, TimeSpan.FromMinutes(1), out _));
        Assert.False(limiter.TryAcquire("post", "legit", 1, TimeSpan.FromMinutes(1), out _));
    }

    [Fact]
    public void TryAcquire_ShouldNotEvictABucketAtItsLimit_WhenTheTableIsFlooded()
    {
        var time = new FakeTime();
        var limiter = new Internal.RequestRateLimiter(time);
        Assert.True(limiter.TryAcquire("exchange", "offender", 1, TimeSpan.FromHours(1), out _));
        Assert.False(limiter.TryAcquire("exchange", "offender", 1, TimeSpan.FromHours(1), out _));
        for (var i = 0; i < Internal.RequestRateLimiter.MaxEntries * 2; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            limiter.TryAcquire("exchange", "k" + i, 5, TimeSpan.FromHours(1), out _);
            if (i % 2000 == 0)
            {
                time.Advance(TimeSpan.FromSeconds(2));
            }
        }

        Assert.False(limiter.TryAcquire("exchange", "offender", 1, TimeSpan.FromHours(1), out _));
    }

    [Fact]
    public void TryAcquire_ShouldThrottleUntrackedKeysCollectively_WhenNoRoomCanBeMade()
    {
        var limiter = new Internal.RequestRateLimiter(new FakeTime());
        for (var i = 0; i < Internal.RequestRateLimiter.MaxEntries; i++)
        {
            limiter.TryAcquire("exchange", "k" + i, 1, TimeSpan.FromHours(1), out _);
            limiter.TryAcquire("exchange", "k" + i, 1, TimeSpan.FromHours(1), out _);
        }

        var passed = 0;
        for (var i = 0; i < 100; i++)
        {
            passed += limiter.TryAcquire("exchange", "new" + i, 1, TimeSpan.FromHours(1), out _) ? 1 : 0;
        }

        Assert.Equal(Internal.RequestRateLimiter.OverflowFactor, passed);
    }

    [Fact]
    public async Task UnprovenGuest_ShouldCountAConcurrentBurstBeforeTheStoreAnswers()
    {
        await using var app = await StartAsync(new FakeTime(), o => o.UnprovenGuestRequestsPerMinute = 3);
        app.Service.Page = new MessagePage(ListOutcome.Refused, []);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationId, "messages"), cookie: GateTests.CookieFor(ConversationId, "tenant-a", GateTests.NewKey()))));

        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.NotFound));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task UnprovenGuest_ShouldNotCountGoodTraffic_ForOneSharedClient()
    {
        await using var app = await StartAsync(new FakeTime(), o => o.UnprovenGuestRequestsPerMinute = 2);
        var statuses = new List<HttpStatusCode>();

        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationId, "messages"), cookie: GateTests.CookieFor(ConversationId, "tenant-a", GateTests.NewKey()))).StatusCode);
        }

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
    }

    [Theory]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:1:2:3:4", "2001:db8:1:2::/64")]
    [InlineData("::ffff:10.0.0.7", "10.0.0.7")]
    [InlineData("10.0.0.7", "10.0.0.7")]
    public void NormalizeAddress_ShouldCollapseAnIpv6SubscriberToItsSlash64(string address, string expected) =>
        Assert.Equal(expected, Internal.RequestRateLimiter.NormalizeAddress(System.Net.IPAddress.Parse(address)));

    [Fact]
    public async Task UnprovenGuest_ShouldShareOneBucketAcrossRouteIds_ForOneClient()
    {
        await using var app = await StartAsync(new FakeTime(), o => o.UnprovenGuestRequestsPerMinute = 2);
        app.Service.Page = new MessagePage(ListOutcome.Refused, []);
        var last = HttpStatusCode.OK;

        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid();
            last = (await app.SendAsync(HttpMethod.Get, TestApp.Path(id, "messages"), cookie: GateTests.CookieFor(id, "tenant-a", GateTests.NewKey()))).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
