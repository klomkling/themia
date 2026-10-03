using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Themia.Storage.Cloudflare.Tests;

/// <summary>
/// The Cloudflare purge call (coord #0153): request shape, and every way a purge can fail, checked against a
/// stub handler. Response shapes follow Cloudflare's published purge documentation.
/// </summary>
public sealed class CloudflareCdnPurgerTests
{
    private const string Token = "cf-secret-token-0123456789";
    private const string SuccessBody = """{"errors":[],"messages":[],"result":{"id":"023e105f4ecef8ad9ca31a8372d0c353"},"success":true}""";
    private const string RateLimitedBody = """{"errors":[{"code":1134,"message":"Unable to purge, rate limit reached. Please wait and consider throttling your request speed"}],"messages":[],"result":null,"success":false}""";
    private const string BadRequestBody = """{"errors":[{"code":1092,"message":"Request cannot contain a bad thing"}],"messages":[],"result":null,"success":false}""";

    private static readonly Uri Photo = new("https://img.example.com/photos/บ้าน 1.jpg");

    private static CloudflareCdnPurger PurgerFor(HttpMessageHandler handler, string zoneId = "zone-1")
    {
        var services = new ServiceCollection();
        services.AddHttpClient(CloudflareCdnPurger.HttpClientName, client => client.BaseAddress = CloudflareCdnPurger.ApiBaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        services.AddSingleton<IOptions<CloudflarePurgeOptions>>(
            Options.Create(new CloudflarePurgeOptions { Enabled = true, ZoneId = zoneId, ApiToken = Token }));
        services.AddSingleton<CloudflareCdnPurger>();
        return services.BuildServiceProvider().GetRequiredService<CloudflareCdnPurger>();
    }

    [Fact]
    public async Task Posts_one_file_to_the_zone_purge_endpoint_with_the_bearer_token()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);

        await PurgerFor(handler).PurgeAsync(Photo);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.cloudflare.com/client/v4/zones/zone-1/purge_cache", request.Uri.AbsoluteUri);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Equal($$"""{"files":["{{Photo.AbsoluteUri}}"]}""", request.Body);
    }

    [Fact]
    public async Task Sends_the_percent_encoded_url_the_edge_was_asked_for_not_the_decoded_one()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);

        await PurgerFor(handler).PurgeAsync(Photo);

        var body = Assert.Single(handler.Requests).Body;
        Assert.Contains("%20", body);
        Assert.DoesNotContain("บ้าน", body);
    }

    [Theory]
    [InlineData(429, RateLimitedBody)]
    [InlineData(500, "")]
    [InlineData(403, """{"errors":[{"code":10000,"message":"Authentication error"}],"success":false}""")]
    [InlineData(200, BadRequestBody)]
    [InlineData(200, "")]
    [InlineData(200, "<html>proxy login</html>")]
    [InlineData(200, """{"errors":[],"messages":[]}""")]
    public async Task A_refused_or_unconfirmed_purge_throws_CdnPurgeException_with_the_status(int status, string body)
    {
        var handler = new RecordingHandler((HttpStatusCode)status, body);

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.Equal(status, thrown.HttpStatus);
        Assert.Equal(Photo, thrown.Url);
        Assert.Contains("already deleted", thrown.Message);
        Assert.DoesNotContain(Token, thrown.ToString());
    }

    [Fact]
    public async Task The_message_carries_cloudflares_first_error_code()
    {
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests, RateLimitedBody);

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.Contains("1134", thrown.Message);
    }

    [Fact]
    public async Task A_zone_id_with_a_slash_and_a_space_is_escaped_into_one_path_segment()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);

        await PurgerFor(handler, zoneId: "zone/1 x").PurgeAsync(Photo);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.cloudflare.com/client/v4/zones/zone%2F1%20x/purge_cache", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task A_huge_error_message_is_capped_and_the_body_is_not_echoed()
    {
        var huge = new string('x', 5000);
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, $$"""{"errors":[{"code":1,"message":"{{huge}}"}],"success":false}""");

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.True(thrown.Message.Length < 700, $"message was {thrown.Message.Length} characters");
    }

    [Fact]
    public async Task A_transport_failure_throws_CdnPurgeException_without_a_status()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.Null(thrown.HttpStatus);
        Assert.IsType<HttpRequestException>(thrown.InnerException);
    }

    [Fact]
    public async Task The_http_clients_own_timeout_throws_CdnPurgeException()
    {
        var handler = new ThrowingHandler(new TaskCanceledException("timed out", new TimeoutException()));

        var thrown = await Assert.ThrowsAsync<CdnPurgeException>(() => PurgerFor(handler).PurgeAsync(Photo));

        Assert.Null(thrown.HttpStatus);
    }

    [Fact]
    public async Task A_callers_own_cancellation_is_not_relabelled_as_a_purge_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new ThrowingHandler(new OperationCanceledException(cts.Token));

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PurgerFor(handler).PurgeAsync(Photo, cts.Token));

        Assert.IsNotType<CdnPurgeException>(thrown);
    }
}
