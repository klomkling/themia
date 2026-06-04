using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Themia.Framework.Core.Abstractions.Tenancy;
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

    [Fact]
    public async Task Middleware_WithUnbridgeableIdentifier_ShouldFailClosedConsistently()
    {
        // A tenant whose identifier is non-null but violates TenantId's rules (here "a.b" — the dot
        // is outside [A-Za-z0-9_-]) makes TenantId.From throw. The middleware must NOT 500: it bridges
        // to no-tenant and leaves BOTH contexts null, so the Tier-3 EF filter denies access.
        var resolved = new TenantInfo("tenant-bad", "a.b", "Misseeded Corp");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantResolver>(new StubTenantResolver(resolved));
        services.AddSingleton<ITenantAccessor>(new StubTenantAccessor());
        await using var provider = services.BuildServiceProvider();

        TenantInfo? observedAccessorCurrent = null;
        TenantId? observedContextId = null;
        var middleware = new TenantResolutionMiddleware(ctx =>
        {
            observedAccessorCurrent = ctx.RequestServices.GetRequiredService<ITenantAccessor>().Current;
            observedContextId = TenantContextAccessor.CurrentTenantId;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext { RequestServices = provider };

        // Must not throw (no unhandled ArgumentException → no 500).
        await middleware.InvokeAsync(httpContext);

        Assert.Null(observedAccessorCurrent);
        Assert.Null(observedContextId);
    }

    [Fact]
    public async Task Middleware_WithValidIdentifier_ShouldBridgeBothContexts()
    {
        var resolved = new TenantInfo("tenant-1", "acme", "Acme Corp");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantResolver>(new StubTenantResolver(resolved));
        services.AddSingleton<ITenantAccessor>(new StubTenantAccessor());
        await using var provider = services.BuildServiceProvider();

        TenantInfo? observedAccessorCurrent = null;
        TenantId? observedContextId = null;
        var middleware = new TenantResolutionMiddleware(ctx =>
        {
            observedAccessorCurrent = ctx.RequestServices.GetRequiredService<ITenantAccessor>().Current;
            observedContextId = TenantContextAccessor.CurrentTenantId;
            return Task.CompletedTask;
        });

        var httpContext = new DefaultHttpContext { RequestServices = provider };

        await middleware.InvokeAsync(httpContext);

        Assert.NotNull(observedAccessorCurrent);
        Assert.Equal("acme", observedAccessorCurrent!.Identifier);
        Assert.NotNull(observedContextId);
        Assert.Equal("acme", observedContextId!.Value.Value);
    }

    private sealed class StubTenantResolver : ITenantResolver
    {
        private readonly TenantInfo? _tenant;

        public StubTenantResolver(TenantInfo? tenant) => _tenant = tenant;

        public Task<TenantInfo?> ResolveAsync(TenantResolutionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(_tenant);
    }

    private sealed class StubTenantAccessor : ITenantAccessor
    {
        public TenantInfo? Current { get; set; }
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
