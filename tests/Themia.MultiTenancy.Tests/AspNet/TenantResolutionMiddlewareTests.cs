using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Themia.MultiTenancy.Abstractions;
using Themia.MultiTenancy.AspNet;

namespace Themia.MultiTenancy.Tests.AspNet;

public class TenantResolutionMiddlewareTests
{
    [Fact]
    public async Task Middleware_WithResolvableTenant_ShouldSetTenantInAccessor()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/test", request =>
        {
            request.Headers.Add("X-Tenant-ID", "acme");
        });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("acme", body);
    }

    [Fact]
    public async Task Middleware_WithoutTenant_ShouldContinueWithNullTenant()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/test");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("no-tenant", body);
    }

    [Fact]
    public async Task Middleware_WithPathBasedTenant_ShouldResolve()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/globex/test");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("globex", body);
    }

    [Fact]
    public async Task Middleware_WithMultipleRequests_ShouldIsolateTenants()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var response1 = await client.GetAsync("/test", request =>
        {
            request.Headers.Add("X-Tenant-ID", "acme");
        });

        var response2 = await client.GetAsync("/test", request =>
        {
            request.Headers.Add("X-Tenant-ID", "globex");
        });

        var body1 = await response1.Content.ReadAsStringAsync();
        var body2 = await response2.Content.ReadAsStringAsync();

        Assert.Equal("acme", body1);
        Assert.Equal("globex", body2);
    }

    [Fact]
    public async Task Middleware_WithDefaultTenant_ShouldFallbackToDefault()
    {
        using var host = await CreateTestHost(configureDefaultTenant: true);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/test");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("default-tenant", body);
    }

    private async Task<IHost> CreateTestHost(bool configureDefaultTenant = false)
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddThemiaMultiTenancy(options =>
                    {
                        if (configureDefaultTenant)
                        {
                            options.DefaultTenantIdentifier = "default-tenant";
                        }
                    }, builder =>
                    {
                        builder.SeedTenants(new[]
                        {
                            new TenantInfo("tenant-1", "acme", "Acme Corp"),
                            new TenantInfo("tenant-2", "globex", "Globex Corp"),
                            new TenantInfo("tenant-3", "default-tenant", "Default Tenant")
                        });
                    });
                });

                webHost.Configure(app =>
                {
                    app.UseThemiaMultiTenancy();

                    app.Run(async context =>
                    {
                        var accessor = context.RequestServices.GetRequiredService<ITenantAccessor>();
                        var tenant = accessor.Current;

                        if (tenant != null)
                        {
                            await context.Response.WriteAsync(tenant.Identifier);
                        }
                        else
                        {
                            await context.Response.WriteAsync("no-tenant");
                        }
                    });
                });
            });

        return await hostBuilder.StartAsync();
    }
}

// Extension method for easier header setting
public static class HttpClientExtensions
{
    public static async Task<HttpResponseMessage> GetAsync(
        this HttpClient client,
        string requestUri,
        Action<HttpRequestMessage> configureRequest)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        configureRequest(request);
        return await client.SendAsync(request);
    }
}
