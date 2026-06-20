using System.Net;
using System.Collections.Specialized;
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
    // Owns the host + its scheduler so both are torn down deterministically. The scheduler is shut
    // down on dispose to avoid leaking Quartz background threads across (parallel) test classes.
    private sealed class TestHostScope(IHost host, IScheduler scheduler) : IAsyncDisposable
    {
        public HttpClient CreateClient() => host.GetTestClient();

        public async ValueTask DisposeAsync()
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
            host.Dispose();
        }
    }

    // Middleware order: UseThemiaQuartz (gate + static files + Services bridge) must run before
    // UseRouting so that the authorize gate and embedded content serving intercept requests before
    // the endpoint dispatcher. MapThemiaQuartz inside UseEndpoints registers the controller route.
    private static async Task<TestHostScope> StartHostAsync(Func<HttpContext, Task<bool>>? authorize, int? deniedStatus = null)
    {
        // Uniquely-named scheduler (not the process-global default) so concurrently-running test
        // classes don't share scheduler state.
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"SmokeTestScheduler_{Guid.NewGuid():N}",
        });
        var scheduler = await factory.GetScheduler();
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
                        if (deniedStatus is { } ds)
                        {
                            o.DeniedStatusCode = ds;
                        }
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

        return new TestHostScope(host, scheduler);
    }

    [Fact]
    public async Task DeniedByDefault_Returns404()
    {
        // Unset Authorize (null) is the documented deny-all default — validate THAT, not an explicit false.
        // Default deny status is 404 (hides the route from unauthenticated probes; matches the
        // Themia.Exceptional dashboard).
        await using var scope = await StartHostAsync(authorize: null);
        var response = await scope.CreateClient().GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Denied_HonorsConfiguredStatusCode()
    {
        await using var scope = await StartHostAsync(authorize: null, deniedStatus: StatusCodes.Status403Forbidden);
        var response = await scope.CreateClient().GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Denied_WhenAuthorizeThrows_FailsClosed()
    {
        // A throwing Authorize predicate must fail closed (deny status), not surface a 500.
        await using var scope = await StartHostAsync(authorize: _ => throw new InvalidOperationException("auth bug"));
        var response = await scope.CreateClient().GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SchedulerIndex_WhenAuthorized_Returns200WithHtml()
    {
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));
        var response = await scope.CreateClient().GetAsync("/jobs/Scheduler/Index");
        response.EnsureSuccessStatusCode();
        Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task EmbeddedContent_IsServed_WhenAuthorized()
    {
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));
        // Asset path: Dashboard/Content/Lib/semanticui/semantic.min.css
        // Embedded resource: Themia.Quartz.Dashboard.Content.Lib.semanticui.semantic.min.css
        // URL: /jobs/Content/Lib/semanticui/semantic.min.css
        var response = await scope.CreateClient().GetAsync("/jobs/Content/Lib/semanticui/semantic.min.css");
        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
    }
}
