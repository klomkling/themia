using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Impl;
using Xunit;

namespace Themia.Quartz.Tests;

/// <summary>
/// Vendoring validation gate: proves the embedded dashboard routes, the deny-all authorize gate,
/// and the embedded static-content file server all work end-to-end via a real in-process host.
/// </summary>
public sealed class DashboardSmokeTests
{
    // Middleware order: UseThemiaQuartz (gate + static files + Services bridge) must run before
    // UseRouting so that the authorize gate and embedded content serving intercept requests before
    // the endpoint dispatcher. MapThemiaQuartz inside UseEndpoints registers the controller route.
    // UseThemiaQuartz is idempotent; MapThemiaQuartz will not call it again in the HostBuilder
    // pattern because endpoints is not an IApplicationBuilder in that context.
    private static async Task<IHost> StartHostAsync(Func<HttpContext, Task<bool>>? authorize)
    {
        var scheduler = await StdSchedulerFactory.GetDefaultScheduler();
        await scheduler.Start();

        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddThemiaQuartz(o =>
                    {
                        o.Scheduler = scheduler;
                        o.VirtualPathRoot = "/jobs";
                        o.Authorize = authorize;
                    });
                    services.AddSingleton<IExecutionHistoryStore>(new InProcExecutionHistoryStore());
                });
                web.Configure(app =>
                {
                    app.UseThemiaQuartz();
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaQuartz());
                });
            })
            .StartAsync();

        return host;
    }

    [Fact]
    public async Task DeniedByDefault_Returns403()
    {
        // Unset Authorize (null) is the documented deny-all default — validate THAT, not an explicit false.
        using var host = await StartHostAsync(authorize: null);
        var response = await host.GetTestClient().GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SchedulerIndex_WhenAuthorized_Returns200WithHtml()
    {
        using var host = await StartHostAsync(authorize: _ => Task.FromResult(true));
        var response = await host.GetTestClient().GetAsync("/jobs/Scheduler/Index");
        response.EnsureSuccessStatusCode();
        Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task EmbeddedContent_IsServed_WhenAuthorized()
    {
        using var host = await StartHostAsync(authorize: _ => Task.FromResult(true));
        // Asset path: Dashboard/Content/Lib/semanticui/semantic.min.css
        // Embedded resource: Themia.Quartz.Dashboard.Content.Lib.semanticui.semantic.min.css
        // URL: /jobs/Content/Lib/semanticui/semantic.min.css
        var response = await host.GetTestClient().GetAsync("/jobs/Content/Lib/semanticui/semantic.min.css");
        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
    }
}
