using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Themia.Conversations.AspNetCore.Internal;

namespace Themia.Conversations.AspNetCore.Tests;

public sealed class GateTests
{
    private static readonly Guid ConversationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Member = "tenant-a:alice";

    internal static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string CookieFor(Guid id, string tenant, string key) =>
        $"{GuestCookie.NameFor(id)}={WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { t = tenant, k = key })))}";

    private static object PostBody(string clientMessageId = "m-1") => new { clientMessageId, body = "hello" };

    [Fact]
    public async Task Post_ShouldReturn403WithoutReadingTheBody_WhenTheCsrfHeaderIsMissing()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(), csrf: false, member: Member);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, app.BodyReads.Count);
        Assert.Empty(app.Service.Calls);
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("read")]
    public async Task UnsafeRoutes_ShouldRequireTheCsrfHeader_ForMembersToo(string tail)
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, tail), new { clientMessageId = "m-1", seq = 1 }, csrf: false, member: Member);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(app.Service.Calls);
    }

    [Fact]
    public async Task Post_ShouldReturn401WithoutReadingTheBody_WhenThereIsNoMemberAndNoCookie()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, app.BodyReads.Count);
        Assert.Empty(app.Service.Calls);
    }

    [Fact]
    public async Task Post_ShouldActAsTheMember_WhenAStaleGuestCookieIsAlsoPresent()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(), member: Member, cookie: CookieFor(ConversationId, "tenant-a", NewKey()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var call = Assert.Single(app.Service.Calls);
        Assert.Equal(ConversationActorKind.Member, call.Actor.Kind);
        Assert.Equal("alice", call.Actor.UserRef);
    }

    [Fact]
    public async Task Post_ShouldActAsTheGuestOfTheCookiesTenant_WhenOnlyTheRouteConversationsCookieIsPresent()
    {
        await using var app = await TestApp.StartAsync();
        var key = NewKey();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(), cookie: CookieFor(ConversationId, "tenant-z", key));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var call = Assert.Single(app.Service.Calls);
        Assert.Equal(ConversationActorKind.Guest, call.Actor.Kind);
        Assert.Equal("tenant-z", call.TenantId);
        Assert.Equal(key, call.Actor.GuestSession!.Reveal());
    }

    [Fact]
    public async Task Post_ShouldReturn401_WhenTheOnlyCookieBelongsToAnotherConversation()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(), cookie: CookieFor(OtherId, "tenant-a", NewKey()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(app.Service.Calls);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("bm90LWpzb24")]
    [InlineData("e30")]
    public async Task Post_ShouldReturn401WithoutAStoreCall_WhenTheCookieValueIsMalformed(string value)
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(), cookie: $"{GuestCookie.NameFor(ConversationId)}={value}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(app.Service.Calls);
    }

    [Theory]
    [InlineData("short-key", false)]
    [InlineData("tenant-a", true)]
    public async Task Post_ShouldReturn401WithoutAStoreCall_WhenTheCookieKeyOrTenantIsOutOfBounds(string tenantOrKey, bool tooLongTenant)
    {
        await using var app = await TestApp.StartAsync();
        var cookie = tooLongTenant
            ? CookieFor(ConversationId, new string('t', GuestCookie.MaxTenantLength + 1), NewKey())
            : CookieFor(ConversationId, "tenant-a", tenantOrKey);

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(), cookie: cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(app.Service.Calls);
    }

    [Fact]
    public async Task Post_ShouldReachTheStore_WhenTheTenantIsAtTheLimitAndAllNonAscii()
    {
        await using var app = await TestApp.StartAsync();
        var cookie = CookieFor(ConversationId, new string('\u0E01', GuestCookie.MaxTenantLength), NewKey());

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(), cookie: cookie);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Single(app.Service.Calls);
    }

    [Theory]
    [InlineData(768, true)]
    [InlineData(769, false)]
    public async Task Post_ShouldApplyTheCookieValueCapExactly_At1024Characters(int payloadBytes, bool reachesTheStore)
    {
        await using var app = await TestApp.StartAsync();
        var key = NewKey();
        var bare = JsonSerializer.Serialize(new { t = "tenant-a", k = key, p = string.Empty });
        var payload = JsonSerializer.Serialize(new { t = "tenant-a", k = key, p = new string('x', payloadBytes - bare.Length) });
        var value = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(), cookie: $"{GuestCookie.NameFor(ConversationId)}={value}");

        Assert.Equal(reachesTheStore ? HttpStatusCode.Created : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(reachesTheStore ? 1 : 0, app.Service.Calls.Count);
    }

    [Theory]
    [InlineData("afterSeq=-1", "invalid_after_seq")]
    [InlineData("afterSeq=abc", "invalid_after_seq")]
    [InlineData("limit=0", "invalid_limit")]
    [InlineData("limit=101", "invalid_limit")]
    [InlineData("limit=x", "invalid_limit")]
    public async Task List_ShouldReturn422WithAStableCode_WhenTheQueryIsOutOfRange(string query, string code)
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationId, "messages") + "?" + query, member: Member);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains($"\"code\":\"{code}\"", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Service.Calls);
    }

    [Fact]
    public async Task List_ShouldPassTheDefaultsAndSecurityHeaders()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationId, "messages"), member: Member);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((0L, 50), Assert.Single(app.Service.Calls).Argument);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" lead")]
    [InlineData("has space")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123x")]
    public async Task Post_ShouldReturn422_WhenTheClientMessageIdIsMalformed(string clientMessageId)
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(clientMessageId), member: Member);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("\"code\":\"invalid_client_message_id\"", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Service.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"seq\":-1}")]
    public async Task Read_ShouldReturn422_WhenSeqIsMissingOrNegative(string json)
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "read"), json, member: Member);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("\"code\":\"invalid_seq\"", await response.Content.ReadAsStringAsync());
        Assert.Empty(app.Service.Calls);
    }

    [Fact]
    public async Task Post_ShouldReturn422_WhenTheBodyHasAnUnknownField()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), "{\"clientMessageId\":\"m-1\",\"body\":\"x\",\"admin\":true}", member: Member);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(app.Service.Calls);
    }

    [Fact]
    public async Task Post_ShouldReturn413WithoutAServiceCall_WhenTheJsonBodyIsOverTheCap()
    {
        await using var app = await TestApp.StartAsync(o => o.MaxJsonBodyBytes = 64);

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), new { clientMessageId = "m-1", body = new string('x', 200) }, member: Member);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(app.Service.Calls);
    }

    [Theory]
    [InlineData(PostOutcome.Posted, HttpStatusCode.Created)]
    [InlineData(PostOutcome.Duplicate, HttpStatusCode.OK)]
    [InlineData(PostOutcome.Closed, HttpStatusCode.Conflict)]
    [InlineData(PostOutcome.InvalidMessage, HttpStatusCode.UnprocessableEntity)]
    [InlineData(PostOutcome.AttachmentRejected, HttpStatusCode.UnprocessableEntity)]
    [InlineData(PostOutcome.QuotaExceeded, HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(PostOutcome.Refused, HttpStatusCode.NotFound)]
    public async Task Post_ShouldMapEachOutcomeToItsStatus(PostOutcome outcome, HttpStatusCode status)
    {
        await using var app = await TestApp.StartAsync();
        app.Service.PostResult = new PostResult(outcome, Guid.NewGuid(), 7);

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "messages"), PostBody(), member: Member);

        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task Refusals_ShouldBeByteIdentical_ForUnknownAndForeignConversations()
    {
        await using var app = await TestApp.StartAsync();
        app.Service.Page = new MessagePage(ListOutcome.Refused, []);
        app.Service.PostResult = new PostResult(PostOutcome.Refused, null, null);

        var list = await app.SendAsync(HttpMethod.Get, TestApp.Path(ConversationId, "messages"), member: Member);
        var post = await app.SendAsync(HttpMethod.Post, TestApp.Path(OtherId, "messages"), PostBody(), member: Member);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(await list.Content.ReadAsStringAsync(), await post.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Read_ShouldMarkAndReturnTheStoredPosition()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.SendAsync(HttpMethod.Post, TestApp.Path(ConversationId, "read"), new { seq = 5 }, member: Member);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"lastReadSeq\":5", await response.Content.ReadAsStringAsync());
        Assert.Equal(5L, Assert.Single(app.Service.Calls).Argument);
    }
}
