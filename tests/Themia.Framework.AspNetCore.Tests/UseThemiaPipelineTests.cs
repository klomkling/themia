using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.AspNetCore.Exceptions;
using Themia.Framework.AspNetCore.Extensions;
using Themia.MultiTenancy;
using Themia.MultiTenancy.Abstractions;
using Xunit;

namespace Themia.Framework.AspNetCore.Tests;

public sealed class UseThemiaPipelineTests
{
    private static async Task<IHost> StartHostAsync(Action<IServiceCollection> configureServices, RequestDelegate terminal)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddThemiaAspNetCore();
                    configureServices(services);
                });
                webHost.Configure(app =>
                {
                    app.UseThemia();
                    app.Run(terminal);
                });
            })
            .StartAsync();
        return host;
    }

    [Fact]
    public async Task ProblemDetails_MapsTypedException_FromDownstream()
    {
        // NotFoundException(string message, string? errorCode = null, ...) — "42" is the errorCode.
        // UseThemiaProblemDetails maps any NotFoundException to HTTP 404 + application/problem+json.
        using var host = await StartHostAsync(
            configureServices: services => services.AddThemiaMultiTenancy(
                configure: b => b.UseHeaderStrategy().SeedTenants([new TenantInfo("1", "acme")])),
            terminal: _ => throw new NotFoundException("widget not found", "42"));

        var response = await host.GetTestClient().GetAsync("/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task TenantResolution_PopulatesAccessor_FromHeader()
    {
        string? observedIdentifier = null;
        using var host = await StartHostAsync(
            configureServices: services => services.AddThemiaMultiTenancy(
                configure: b => b.UseHeaderStrategy().SeedTenants([new TenantInfo("1", "acme")])),
            terminal: context =>
            {
                observedIdentifier = context.RequestServices.GetRequiredService<ITenantAccessor>().Current?.Identifier;
                return Task.CompletedTask;
            });

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Tenant-ID", "acme");
        var response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acme", observedIdentifier);
    }
}
