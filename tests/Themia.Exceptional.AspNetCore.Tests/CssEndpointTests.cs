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
    }
}
