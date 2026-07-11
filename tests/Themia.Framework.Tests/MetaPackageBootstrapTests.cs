using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Framework.AspNetCore.Extensions;
using Themia.MultiTenancy;
using Themia.MultiTenancy.Abstractions;
using Xunit;

namespace Themia.Framework.Tests;

/// <summary>
/// Runtime proof of the README quickstart: referencing only the Themia.Framework
/// metapackage + one data peer, the Themia bootstrap (AddThemiaAspNetCore +
/// AddThemiaMultiTenancy + UseThemia) builds a host that serves a request and
/// resolves the tenant from the header strategy.
/// </summary>
public sealed class MetaPackageBootstrapTests
{
    [Fact]
    public async Task Quickstart_BootsHostAndResolvesTenant_WithMetapackageAndOnePeer()
    {
        string? observedIdentifier = null;
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddThemiaAspNetCore();
                    services.AddThemiaMultiTenancy(
                        configure: b => b.UseHeaderStrategy().SeedTenants([new TenantInfo("1", "acme")]));
                });
                webHost.Configure(app =>
                {
                    app.UseThemia();
                    app.Run(context =>
                    {
                        observedIdentifier = context.RequestServices
                            .GetRequiredService<ITenantAccessor>().Current?.Identifier;
                        return Task.CompletedTask;
                    });
                });
            })
            .StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Tenant-ID", "acme");
        var response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acme", observedIdentifier);
    }
}
