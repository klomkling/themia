using System.Net;
using System.Text.Json;
using Xunit;

namespace Themia.Content.AspNetCore.Tests;

public class ContentPublicEndpointTests
{
    [Fact]
    public async Task Get_ShouldReturnThePageInADataEnvelope_WhenPublished()
    {
        var service = new FakeContentPageService { Page = FakeContentPageService.SamplePage() };
        var client = await ContentTestServer.StartAsync(service, e => e.MapThemiaContentPublicEndpoints("/api/v1/pages"));

        var response = await client.GetAsync("/api/v1/pages/terms?lang=en");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("terms", json.RootElement.GetProperty("data").GetProperty("slug").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("data").GetProperty("currentVersion").GetInt32());
        Assert.Equal(("terms", "en"), service.LastPublishedRequest);
    }

    [Fact]
    public async Task Get_ShouldPassNoLanguage_WhenTheQueryHasNone()
    {
        var service = new FakeContentPageService { Page = FakeContentPageService.SamplePage() };
        var client = await ContentTestServer.StartAsync(service, e => e.MapThemiaContentPublicEndpoints("/api/v1/pages"));

        await client.GetAsync("/api/v1/pages/terms");

        Assert.Equal(("terms", (string?)null), service.LastPublishedRequest);
    }

    [Fact]
    public async Task Get_ShouldReturnProblemDetails404_WhenNoPageIsPublished()
    {
        var client = await ContentTestServer.StartAsync(new FakeContentPageService(), e => e.MapThemiaContentPublicEndpoints("/api/v1/pages"));

        var response = await client.GetAsync("/api/v1/pages/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
