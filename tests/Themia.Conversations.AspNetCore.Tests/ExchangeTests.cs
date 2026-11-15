using System.Net;
using Themia.Conversations.AspNetCore.Internal;

namespace Themia.Conversations.AspNetCore.Tests;

public sealed class ExchangeTests
{
    private static readonly Guid ConversationA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConversationB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateTimeOffset Expiry = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static object Body(string token = "link-token", string tenant = "tenant-a") => new { tenantId = tenant, token };

    private static string CookiePair(HttpResponseMessage response, Guid conversationId) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(GuestCookie.NameFor(conversationId), StringComparison.Ordinal)).Split(';')[0];

    [Fact]
    public async Task Exchange_ShouldSetAHardenedPerConversationCookie_AndReturnNoKey()
    {
        await using var app = await TestApp.StartAsync();
        var key = GateTests.NewKey();
        app.Guests.Allow("tenant-a", "link-token", ConversationA, new GuestSessionKey(key), Expiry);

        var response = await app.SendAsync(HttpMethod.Post, "/api/v1/guest/exchange", Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-themia_conv_" + ConversationA.ToString("N") + "=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(ConversationA.ToString(), body);
        Assert.DoesNotContain(key, body, StringComparison.Ordinal);
        Assert.DoesNotContain(key, string.Join('\n', response.Headers.Where(h => h.Key != "Set-Cookie").SelectMany(h => h.Value)), StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Exchange_ShouldKeepBothConversationsUsable_WhenTwoLinksAreExchangedInOneJar()
    {
        await using var app = await TestApp.StartAsync();
        app.Guests.Allow("tenant-a", "link-a", ConversationA, new GuestSessionKey(GateTests.NewKey()), Expiry);
        app.Guests.Allow("tenant-a", "link-b", ConversationB, new GuestSessionKey(GateTests.NewKey()), Expiry);

        var first = await app.SendAsync(HttpMethod.Post, "/api/v1/guest/exchange", Body("link-a"));
        var second = await app.SendAsync(HttpMethod.Post, "/api/v1/guest/exchange", Body("link-b"));
        var jar = CookiePair(first, ConversationA) + "; " + CookiePair(second, ConversationB);
        var readA = await app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationA, "messages"), cookie: jar);
        var readB = await app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationB, "messages"), cookie: jar);

        Assert.Equal(HttpStatusCode.OK, readA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readB.StatusCode);
        Assert.Equal(new[] { ConversationA, ConversationB }, app.Service.Calls.Select(c => c.ConversationId));
    }

    [Fact]
    public async Task Exchange_ShouldReturnOneIdenticalNotFound_ForEveryRefusal()
    {
        await using var app = await TestApp.StartAsync();
        app.Guests.Allow("tenant-a", "good", ConversationA, new GuestSessionKey(GateTests.NewKey()), Expiry);

        var badToken = await app.SendAsync(HttpMethod.Post, "/api/v1/guest/exchange", Body("bad"));
        var wrongTenant = await app.SendAsync(HttpMethod.Post, "/api/v1/guest/exchange", Body("good", "tenant-b"));

        Assert.Equal(HttpStatusCode.NotFound, badToken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongTenant.StatusCode);
        Assert.Equal(await badToken.Content.ReadAsStringAsync(), await wrongTenant.Content.ReadAsStringAsync());
        Assert.False(badToken.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Exchange_ShouldReturn403WithoutReadingTheBody_WhenTheCsrfHeaderIsMissing()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, "/api/v1/guest/exchange", Body(), csrf: false);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, app.BodyReads.Count);
        Assert.Empty(app.Guests.Calls);
    }

    [Theory]
    [InlineData("{\"tenantId\":\"tenant-a\"}")]
    [InlineData("{\"tenantId\":\"\",\"token\":\"x\"}")]
    [InlineData("{\"tenantId\":\"tenant-a\",\"token\":\"x\",\"extra\":1}")]
    public async Task Exchange_ShouldReturn422WithoutAStoreCall_WhenTheBodyIsIncomplete(string json)
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, "/api/v1/guest/exchange", json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(app.Guests.Calls);
    }

    [Fact]
    public async Task NoPath_ShouldWriteTheKeyOrTheLinkTokenToTheLogs()
    {
        await using var app = await TestApp.StartAsync();
        var key = GateTests.NewKey();
        app.Guests.Allow("tenant-a", "secret-link-token", ConversationA, new GuestSessionKey(key), Expiry);

        var exchange = await app.SendAsync(HttpMethod.Post, "/api/v1/guest/exchange", Body("secret-link-token"));
        await app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationA, "messages"), cookie: CookiePair(exchange, ConversationA));
        await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationA, "messages"), new { clientMessageId = "", body = "x" }, cookie: CookiePair(exchange, ConversationA));
        await app.SendAsync(HttpMethod.Post, "/api/v1/guest/exchange", Body("secret-link-token-2"));

        var logs = string.Join('\n', app.Logs.Lines);
        Assert.DoesNotContain(key, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-link-token", logs, StringComparison.Ordinal);
    }
}
