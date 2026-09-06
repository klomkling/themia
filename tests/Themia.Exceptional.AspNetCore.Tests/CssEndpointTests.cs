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

public class CssEndpointTests
{
    private static async Task<HttpClient> ServerAsync(IExceptionStore store, Action<ExceptionalDashboardOptions>? configure)
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
                    app.UseEndpoints(e => e.MapThemiaExceptional("/exceptions", configure));
                });
            })
            .StartAsync();
        return host.GetTestClient();
    }

    [Fact]
    public async Task Css_IsServed_WithCssContentType()
    {
        var client = await ServerAsync(new FakeExceptionStore(), o => o.Authorize = _ => Task.FromResult(true));

        var res = await client.GetAsync("/exceptions/dashboard.css");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("text/css", res.Content.Headers.ContentType!.ToString());
        Assert.Contains("table", await res.Content.ReadAsStringAsync());
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
    }

    // Serving the stylesheet unauthenticated returned 200 for a path whose siblings return 404, which
    // confirms the dashboard's mount point to anyone who asks. Assert the body too: a status-only check
    // would pass while the CSS was still on the wire.
    [Fact]
    public async Task Css_IsDenied_WhenAuthorizeIsUnset()
    {
        var client = await ServerAsync(new FakeExceptionStore(), configure: null);

        var res = await client.GetAsync("/exceptions/dashboard.css");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain("table", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Css_IsDenied_WhenAuthorizeReturnsFalse()
    {
        var client = await ServerAsync(new FakeExceptionStore(), o => o.Authorize = _ => Task.FromResult(false));

        var res = await client.GetAsync("/exceptions/dashboard.css");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // Gating at the origin is not enough on its own: a shared cache holding an authorized 200 can serve it
    // to an unauthenticated caller without Authorize ever running, and a cached 404 can be handed back to a
    // legitimate admin. Assert the header, not merely the status — a status-only test passes with every
    // response fully cacheable.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Every_css_response_is_uncacheable(bool authorized)
    {
        var client = await ServerAsync(
            new FakeExceptionStore(),
            o => o.Authorize = _ => Task.FromResult(authorized));

        var res = await client.GetAsync("/exceptions/dashboard.css");

        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        Assert.Contains("Cookie", res.Headers.Vary.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_deny_404_on_the_list_route_is_uncacheable()
    {
        var client = await ServerAsync(new FakeExceptionStore(), configure: null);

        var res = await client.GetAsync("/exceptions");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
    }
}
