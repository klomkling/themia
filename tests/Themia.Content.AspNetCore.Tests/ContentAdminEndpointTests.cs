using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Themia.Content.AspNetCore.Tests;

public class ContentAdminEndpointTests
{
    private const string Prefix = "/api/v1/admin/content";

    private static readonly ContentAdminOptions AllowAll = new() { Authorize = _ => Task.FromResult(true) };

    private static Task<HttpClient> StartAsync(FakeContentPageService service, ContentAdminOptions options) =>
        ContentTestServer.StartAsync(service, e => e.MapThemiaContentAdminEndpoints(options, Prefix));

    private static HttpRequestMessage Request(HttpMethod method, string path, string? json = null, string? user = "user-7")
    {
        var request = new HttpRequestMessage(method, Prefix + path);
        if (user is not null)
        {
            request.Headers.Add(ContentTestServer.UserHeader, user);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private const string ValidBody = """{"title":"Terms","markdown":"# Terms","isPublished":true,"expectedVersion":3,"changeSummary":"wording"}""";

    [Fact]
    public async Task AdminRoutes_ShouldRefuseAnonymousWith401_WhenAuthorizeIsUnset()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions());

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages", user: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AdminRoutes_ShouldRefuseAuthenticatedWith403_WhenAuthorizeIsUnset()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions());

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AdminRoutes_ShouldRefuseWith403_WhenAuthorizeReturnsFalse()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions { Authorize = _ => Task.FromResult(false) });

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AdminRoutes_ShouldFailClosed_WhenAuthorizeThrows()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions { Authorize = _ => throw new InvalidOperationException("broken") });

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Put_ShouldSaveWithTheRouteKeyTheBodyAndTheEditorFromNameIdentifier()
    {
        var service = new FakeContentPageService { SaveResult = ContentSaveResult.Saved(FakeContentPageService.SamplePage()) };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/en", ValidBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ContentPageSave("terms", "en", "Terms", "# Terms", true, 3, "wording", "user-7"), service.LastSave);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("terms", json.RootElement.GetProperty("data").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Put_ShouldUseResolveEditorId_WhenOverridden()
    {
        var service = new FakeContentPageService { SaveResult = ContentSaveResult.Saved(FakeContentPageService.SamplePage()) };
        var options = new ContentAdminOptions { Authorize = _ => Task.FromResult(true), ResolveEditorId = _ => "resolved" };
        var client = await StartAsync(service, options);

        await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal("resolved", service.LastSave!.EditorId);
    }

    [Fact]
    public async Task Put_ShouldReturn422WithTheMarkdownField_WhenInvalid()
    {
        var service = new FakeContentPageService
        {
            SaveResult = ContentSaveResult.Invalid([new ContentValidationError("markdown", "Markdown must not contain raw HTML.")]),
        };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Markdown must not contain raw HTML.", json.RootElement.GetProperty("errors").GetProperty("markdown")[0].GetString());
    }

    [Fact]
    public async Task Put_ShouldReturn409WithTheCurrentState_WhenConflict()
    {
        var updatedAt = new DateTimeOffset(2026, 9, 14, 3, 0, 0, TimeSpan.Zero);
        var service = new FakeContentPageService { SaveResult = ContentSaveResult.Conflict(4, "editor-b", updatedAt) };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(4, json.RootElement.GetProperty("currentVersion").GetInt32());
        Assert.Equal("editor-b", json.RootElement.GetProperty("updatedBy").GetString());
        Assert.Equal(updatedAt, json.RootElement.GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Put_ShouldReturn404_WhenNotFound()
    {
        var client = await StartAsync(new FakeContentPageService { SaveResult = ContentSaveResult.NotFound() }, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_ShouldReturn400AndNotSave_WhenTheBodyHasAnUnknownMember()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(
            HttpMethod.Put, "/pages/terms/th",
            """{"title":"T","markdown":"# T","isPublished":true,"expectedVersion":0,"bogus":1}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(service.LastSave);
    }

    [Fact]
    public async Task Put_ShouldSendExpectedVersionZero_WhenTheBodyOmitsIt()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", """{"title":"T","markdown":"# T","isPublished":true}"""));

        Assert.Equal(0, service.LastSave!.ExpectedVersion);
    }

    [Fact]
    public async Task List_ShouldReturnDataAndMeta()
    {
        var summary = new ContentPageSummary("terms", "th", "Terms", 2, true, DateTimeOffset.UnixEpoch, null);
        var service = new FakeContentPageService { Summaries = new PagedResult<ContentPageSummary> { Items = [summary], Total = 41 } };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages?page=3&limit=20"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((3, 20), service.LastListRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var meta = json.RootElement.GetProperty("meta");
        Assert.Equal(3, meta.GetProperty("page").GetInt32());
        Assert.Equal(20, meta.GetProperty("limit").GetInt32());
        Assert.Equal(41, meta.GetProperty("total").GetInt32());
        Assert.Equal("terms", json.RootElement.GetProperty("data")[0].GetProperty("slug").GetString());
    }

    [Fact]
    public async Task List_ShouldDefaultToPageOneAndTwentyItems()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        await client.SendAsync(Request(HttpMethod.Get, "/pages"));

        Assert.Equal((1, 20), service.LastListRequest);
    }

    [Theory]
    [InlineData("/pages?limit=0", "limit")]
    [InlineData("/pages?limit=101", "limit")]
    [InlineData("/pages?page=0", "page")]
    [InlineData("/pages/terms/th/revisions?limit=101", "limit")]
    public async Task Paging_ShouldReturn422_WhenOutOfRange(string path, string field)
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Get, path));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task GetPage_ShouldReturn404_WhenMissing()
    {
        var client = await StartAsync(new FakeContentPageService(), AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages/terms/th"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_ShouldReturn401AndNotReadTheBody_WhenAnonymousSendsMalformedJson()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions());

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", "{not json", user: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(service.LastSave);
    }

    [Fact]
    public async Task Put_ShouldReturnProblemDetails400_WhenTheBodyHasAnUnknownMember()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(
            HttpMethod.Put, "/pages/terms/th",
            """{"title":"T","markdown":"# T","isPublished":true,"expectedVersion":0,"bogus":1}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(service.LastSave);
    }

    [Fact]
    public async Task Put_ShouldReturnProblemDetails400_WhenTheBodyIsMalformed()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", "{not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(service.LastSave);
    }

    [Fact]
    public async Task List_ShouldReturn422_WhenPageIsNotANumber()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages?page=abc"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AdminRoutes_ShouldRefuseWith403_WhenAuthorizeThrowsOperationCanceledWithoutAbort()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions { Authorize = _ => throw new OperationCanceledException() });

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Revert_ShouldMapTheBodysVersionToTheTargetVersion()
    {
        var service = new FakeContentPageService { SaveResult = ContentSaveResult.Saved(FakeContentPageService.SamplePage()) };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(
            HttpMethod.Post, "/pages/terms/en/revert", """{"version":1,"expectedVersion":3,"changeSummary":null}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ContentPageRevert("terms", "en", 1, 3, null, "user-7"), service.LastRevert);
    }
}
