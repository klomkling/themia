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

/// <summary>
/// Integration tests for middleware behavior, strategy ordering, and context population.
/// </summary>
public class MiddlewareIntegrationTests
{
    [Fact]
    public async Task Middleware_ShouldPopulateITenantAccessor()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/test")
        {
            Headers = { { "X-Tenant-ID", "acme" } }
        });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var tenantId = await response.Content.ReadAsStringAsync();
        Assert.Equal("acme", tenantId);
    }

    [Fact]
    public async Task Middleware_OnResolution_ShouldBridgeTenantIdIntoTenantContextAccessor()
    {
        using var host = await CreateTestHostBridging();
        var client = host.GetTestClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/test")
        {
            Headers = { { "X-Tenant-ID", "acme" } }
        });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var bridged = await response.Content.ReadAsStringAsync();

        // The middleware set TenantContextAccessor.CurrentTenantId = TenantId.From("acme").
        Assert.Equal("acme", bridged);
    }

    [Fact]
    public async Task Middleware_StrategyOrdering_HeaderBeforePath()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        // Both header and path contain tenant IDs
        // Header strategy should win (comes first)
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/globex/test")
        {
            Headers = { { "X-Tenant-ID", "acme" } }
        });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var tenantId = await response.Content.ReadAsStringAsync();

        // Should use header (acme) not path (globex)
        Assert.Equal("acme", tenantId);
    }

    [Fact]
    public async Task Middleware_StrategyOrdering_PathWhenNoHeader()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        // No header, should fall back to path strategy
        var response = await client.GetAsync("/globex/test");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var tenantId = await response.Content.ReadAsStringAsync();

        Assert.Equal("globex", tenantId);
    }

    [Fact]
    public async Task Middleware_StrategyOrdering_DefaultWhenNeitherHeaderNorPath()
    {
        using var host = await CreateTestHostWithDefaultTenant();
        var client = host.GetTestClient();

        // No header, no path match - should use default
        var response = await client.GetAsync("/test");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var tenantId = await response.Content.ReadAsStringAsync();

        Assert.Equal("default-tenant", tenantId);
    }

    [Fact]
    public async Task Middleware_CustomStrategyOrdering_CustomAfterDefaults()
    {
        using var host = await CreateTestHostWithCustomStrategy();
        var client = host.GetTestClient();

        // No header, no path, no default - should use custom strategy
        var response = await client.GetAsync("/test");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var tenantId = await response.Content.ReadAsStringAsync();

        // Custom strategy returns "custom-tenant"
        Assert.Equal("custom-tenant", tenantId);
    }

    [Fact]
    public async Task Middleware_WithoutTenantResolution_ShouldHandleGracefully()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        // No tenant resolution possible
        var response = await client.GetAsync("/test");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadAsStringAsync();

        Assert.Equal("no-tenant", result);
    }

    [Fact]
    public async Task Middleware_RequestIsolation_DifferentTenantsInConcurrentRequests()
    {
        using var host = await CreateTestHost();
        var client = host.GetTestClient();

        var tasks = new[]
        {
            SendRequestWithTenant(client, "tenant1"),
            SendRequestWithTenant(client, "tenant2"),
            SendRequestWithTenant(client, "tenant3"),
            SendRequestWithTenant(client, "tenant4"),
            SendRequestWithTenant(client, "tenant5")
        };

        var results = await Task.WhenAll(tasks);

        Assert.Equal("tenant1", results[0]);
        Assert.Equal("tenant2", results[1]);
        Assert.Equal("tenant3", results[2]);
        Assert.Equal("tenant4", results[3]);
        Assert.Equal("tenant5", results[4]);
    }

    private async Task<string> SendRequestWithTenant(HttpClient client, string tenantId)
    {
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/test")
        {
            Headers = { { "X-Tenant-ID", tenantId } }
        });

        return await response.Content.ReadAsStringAsync();
    }

    private async Task<IHost> CreateTestHost()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddThemiaMultiTenancy(configure: builder =>
                    {
                        builder.SeedTenants(new[]
                        {
                            new TenantInfo("1", "acme", "Acme Corp"),
                            new TenantInfo("2", "globex", "Globex Corp"),
                            new TenantInfo("3", "tenant1", "Tenant 1"),
                            new TenantInfo("4", "tenant2", "Tenant 2"),
                            new TenantInfo("5", "tenant3", "Tenant 3"),
                            new TenantInfo("6", "tenant4", "Tenant 4"),
                            new TenantInfo("7", "tenant5", "Tenant 5")
                        });
                    });
                });

                webHost.Configure(app =>
                {
                    app.UseThemiaMultiTenancy();

                    app.Run(async context =>
                    {
                        var accessor = context.RequestServices.GetRequiredService<ITenantAccessor>();
                        if (accessor.Current != null)
                        {
                            await context.Response.WriteAsync(accessor.Current.Identifier);
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

    private async Task<IHost> CreateTestHostBridging()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddThemiaMultiTenancy(configure: builder =>
                    {
                        builder.SeedTenants(new[] { new TenantInfo("1", "acme", "Acme Corp") });
                    });
                });

                webHost.Configure(app =>
                {
                    app.UseThemiaMultiTenancy();

                    app.Run(async context =>
                    {
                        var tenantId = Themia.Framework.Core.Abstractions.Tenancy
                            .TenantContextAccessor.CurrentTenantId;
                        await context.Response.WriteAsync(tenantId?.Value ?? "no-bridge");
                    });
                });
            });

        return await hostBuilder.StartAsync();
    }

    private async Task<IHost> CreateTestHostWithDefaultTenant()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddThemiaMultiTenancy(
                        options =>
                        {
                            options.DefaultTenantIdentifier = "default-tenant";
                        },
                        configure: builder =>
                        {
                            builder.SeedTenants(new[]
                            {
                                new TenantInfo("1", "default-tenant", "Default Tenant")
                            });
                        });
                });

                webHost.Configure(app =>
                {
                    app.UseThemiaMultiTenancy();

                    app.Run(async context =>
                    {
                        var accessor = context.RequestServices.GetRequiredService<ITenantAccessor>();
                        if (accessor.Current != null)
                        {
                            await context.Response.WriteAsync(accessor.Current.Identifier);
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

    private async Task<IHost> CreateTestHostWithCustomStrategy()
    {
        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddThemiaMultiTenancy(configure: builder =>
                    {
                        builder.AddStrategy<AlwaysReturnsCustomTenantStrategy>();
                        builder.SeedTenants(new[]
                        {
                            new TenantInfo("1", "custom-tenant", "Custom Tenant")
                        });
                    });
                });

                webHost.Configure(app =>
                {
                    app.UseThemiaMultiTenancy();

                    app.Run(async context =>
                    {
                        var accessor = context.RequestServices.GetRequiredService<ITenantAccessor>();
                        if (accessor.Current != null)
                        {
                            await context.Response.WriteAsync(accessor.Current.Identifier);
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

/// <summary>
/// Test strategy that always returns a specific tenant identifier.
/// </summary>
internal sealed class AlwaysReturnsCustomTenantStrategy : ITenantResolutionStrategy
{
    public Task<TenantResolutionResult> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(TenantResolutionResult.Identified("custom-tenant", "custom-strategy"));
    }
}
