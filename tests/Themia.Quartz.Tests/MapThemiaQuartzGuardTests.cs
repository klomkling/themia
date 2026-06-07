using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Impl;
using Xunit;

namespace Themia.Quartz.Tests;

/// <summary>
/// Verifies that <see cref="ThemiaQuartzApplicationBuilderExtensions.UseThemiaQuartz"/> fails fast
/// with a clear <see cref="InvalidOperationException"/> when the configuration is invalid.
/// </summary>
public sealed class MapThemiaQuartzGuardTests
{
    [Fact]
    public async Task NoScheduler_Throws()
    {
        // Arrange: no Scheduler set on options and no IScheduler registered in DI.
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddThemiaQuartz(o =>
                    {
                        o.VirtualPathRoot = "/jobs";
                        o.Authorize = _ => Task.FromResult(true);
                        // o.Scheduler intentionally NOT set
                    });
                    services.AddSingleton<IExecutionHistoryStore>(new InProcExecutionHistoryStore());
                });
                web.Configure(app =>
                {
                    app.UseThemiaQuartz();
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaQuartz());
                });
            });

        // Act & Assert: the middleware guard fires during host startup.
        // The exception may be wrapped by the host startup machinery, so use ThrowsAnyAsync
        // and walk the chain for the InvalidOperationException that mentions IScheduler.
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await builder.StartAsync());
        var message = UnwrapMessage(ex);
        Assert.Contains("IScheduler", message);
    }

    [Fact]
    public async Task RootVirtualPathRoot_Throws()
    {
        // Arrange: valid scheduler but empty VirtualPathRoot collapses to "/" — denied.
        // Use a uniquely-named scheduler to avoid colliding with the static DefaultQuartzScheduler
        // that DashboardSmokeTests binds in the same process.
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"GuardTestScheduler_{Guid.NewGuid():N}",
        });
        var scheduler = await factory.GetScheduler();
        await scheduler.Start();

        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddThemiaQuartz(o =>
                    {
                        o.Scheduler = scheduler;
                        o.VirtualPathRoot = "";   // collapses to "/" — must be rejected
                        o.Authorize = _ => Task.FromResult(true);
                    });
                    services.AddSingleton<IExecutionHistoryStore>(new InProcExecutionHistoryStore());
                });
                web.Configure(app =>
                {
                    app.UseThemiaQuartz();
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaQuartz());
                });
            });

        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await builder.StartAsync());
        var message = UnwrapMessage(ex);
        Assert.Contains("VirtualPathRoot", message);
    }

    // Walk the exception chain (may be wrapped by host startup) for the first InvalidOperationException.
    private static string UnwrapMessage(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is InvalidOperationException)
                return current.Message;
            current = current.InnerException;
        }

        // Fall back to the outermost message so the assertion still surfaces something useful.
        return ex.Message;
    }
}
