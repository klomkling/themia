using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Exceptional;
using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public sealed class ActionCsrfTests
{
    // Builds the dashboard host with actions enabled and returns a non-redirecting client so the
    // 303 SeeOther is observable (TestServer follows redirects by default, which would mask it).
    private static async Task<HttpClient> ServerAsync(IExceptionStore store, Func<Microsoft.AspNetCore.Http.HttpContext, Task<bool>> authorize)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton(store);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaExceptional("/exceptions", o =>
                    {
                        o.Authorize = authorize;
                        o.EnableActions = true;
                    }));
                });
            })
            .StartAsync();

        return host.GetTestServer().CreateClient(); // AllowAutoRedirect defaults off on TestServer's client handler
    }

    // Variant of the host with actions disabled, to assert the POST routes are not mapped at all.
    private static async Task<HttpClient> ServerWithActionsDisabledAsync(IExceptionStore store)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton(store);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaExceptional("/exceptions", o =>
                    {
                        o.Authorize = _ => Task.FromResult(true);
                        o.EnableActions = false;
                    }));
                });
            })
            .StartAsync();

        return host.GetTestServer().CreateClient();
    }

    [Fact]
    public async Task Post_WithoutToken_IsRejected()
    {
        var client = await ServerAsync(new FakeExceptionStore(), authorize: _ => Task.FromResult(true));

        var res = await client.PostAsync($"/exceptions/{Guid.NewGuid()}/protect", new FormUrlEncodedContent([]));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_WhenAuthorizeDenies_Returns404_EvenWithToken()
    {
        var client = await ServerAsync(new FakeExceptionStore(), authorize: _ => Task.FromResult(false));

        // Even a well-formed token can't bypass the auth gate (auth runs before the CSRF check).
        var req = new HttpRequestMessage(HttpMethod.Post, $"/exceptions/{Guid.NewGuid()}/protect")
        { Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("__token", "x")]) };
        req.Headers.Add("Cookie", "__themia_csrf=x");
        req.Headers.Add("Origin", "http://localhost");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Post_WithMatchingTokenAndOrigin_InvokesStoreAndRedirects()
    {
        var store = new FakeExceptionStore();
        var client = await ServerAsync(store, authorize: _ => Task.FromResult(true));
        var guid = Guid.NewGuid();

        var req = new HttpRequestMessage(HttpMethod.Post, $"/exceptions/{guid}/protect")
        { Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("__token", "tok")]) };
        req.Headers.Add("Cookie", "__themia_csrf=tok");
        req.Headers.Add("Origin", "http://localhost");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode); // 303 back to the list
        Assert.Equal("/exceptions", res.Headers.Location?.ToString());
        Assert.Contains(guid, store.Protected);
    }

    [Fact]
    public async Task Post_WithCrossOriginOrigin_IsRejected()
    {
        var store = new FakeExceptionStore();
        var client = await ServerAsync(store, authorize: _ => Task.FromResult(true));

        // Valid double-submit token, but the Origin host does not match the request host.
        var req = new HttpRequestMessage(HttpMethod.Post, $"/exceptions/{Guid.NewGuid()}/protect")
        { Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("__token", "tok")]) };
        req.Headers.Add("Cookie", "__themia_csrf=tok");
        req.Headers.Add("Origin", "http://evil.example");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(store.Protected);
    }

    [Fact]
    public async Task Post_WithNeitherOriginNorReferer_IsRejected()
    {
        var store = new FakeExceptionStore();
        var client = await ServerAsync(store, authorize: _ => Task.FromResult(true));

        // Valid token but no Origin/Referer header → cannot prove same-origin.
        var req = new HttpRequestMessage(HttpMethod.Post, $"/exceptions/{Guid.NewGuid()}/protect")
        { Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("__token", "tok")]) };
        req.Headers.Add("Cookie", "__themia_csrf=tok");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(store.Protected);
    }

    [Fact]
    public async Task Post_WhenActionsDisabled_Returns404()
    {
        var store = new FakeExceptionStore();
        var client = await ServerWithActionsDisabledAsync(store);

        var req = new HttpRequestMessage(HttpMethod.Post, $"/exceptions/{Guid.NewGuid()}/delete")
        { Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("__token", "tok")]) };
        req.Headers.Add("Cookie", "__themia_csrf=tok");
        req.Headers.Add("Origin", "http://localhost");
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode); // route not mapped when actions disabled
        Assert.Empty(store.Deleted);
    }
}
