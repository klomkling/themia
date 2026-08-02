using System.Net;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Themia.Messaging.AspNetCore.DependencyInjection;
using Themia.Messaging.Hmac;
using Themia.Messaging.Hmac.DependencyInjection;

using Xunit;

namespace Themia.Messaging.AspNetCore.Tests;

/// <summary>
/// Proves the DI extension (<c>AddThemiaMessagingVerification</c>) and the route extension
/// (<c>RequireThemiaHmac</c>) actually wire the filter through real minimal-API routing and DI resolution
/// — the other tests in this project construct <see cref="HmacVerificationFilter"/> directly, which never
/// exercises endpoint metadata or ActivatorUtilities. The full signed round trip against a real
/// dispatcher is Task 6's job, not this one.
/// </summary>
public class RequireThemiaHmacWiringTests
{
    private const string PeerName = "peer";
    private const string Secret = "wiring-secret";

    [Fact]
    public async Task RequireThemiaHmac_ShouldVerifyAndInvokeEndpoint_ForAValidlySignedRequest()
    {
        using var host = await BuildHost();
        using var client = host.GetTestClient();

        const string body = "{\"hello\":\"world\"}";
        var timestamp = ThemiaHmacV1.FormatTimestamp(DateTimeOffset.UtcNow);
        var signature = ThemiaHmacV1.Sign(ThemiaHmacV1.Canonicalize(timestamp, "POST", "/ingest", body), Secret);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Themia-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Themia-Signature", signature);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"received:{body}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RequireThemiaHmac_ShouldReject401_ForAnInvalidSignature_WithoutInvokingTheEndpoint()
    {
        using var host = await BuildHost();
        using var client = host.GetTestClient();

        const string body = "{\"hello\":\"world\"}";
        var timestamp = ThemiaHmacV1.FormatTimestamp(DateTimeOffset.UtcNow);
        var signature = ThemiaHmacV1.Sign(ThemiaHmacV1.Canonicalize(timestamp, "POST", "/ingest", body), "wrong-secret");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Themia-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Themia-Signature", signature);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<IHost> BuildHost()
        => await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddThemiaMessagingHmac(o => o.AddPeer(PeerName, p =>
                    {
                        p.SignWith("out-1", Secret);
                        p.Accept("in-1", Secret);
                    }));
                    services.AddThemiaMessagingVerification();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapPost("/ingest", async (HttpContext context) =>
                        {
                            using var reader = new StreamReader(context.Request.Body);
                            var body = await reader.ReadToEndAsync();
                            return Results.Text($"received:{body}");
                        }).RequireThemiaHmac(PeerName);
                    });
                }))
            .StartAsync();
}
