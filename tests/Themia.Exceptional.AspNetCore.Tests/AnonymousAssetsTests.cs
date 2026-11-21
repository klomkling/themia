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

public class AnonymousAssetsTests
{
    private const string Marker = "ANON-ASSET-SECRET-MARKER";
    private static readonly Guid EntryGuid = new("0f8fad5b-d9cb-469f-a165-70867728950e");

    private sealed class Probe
    {
        public int AuthorizeCalls;
    }

    private static async Task<HttpClient> ServerAsync(Probe probe, Action<ExceptionalDashboardOptions>? configure, bool authorized = false)
    {
        var store = new FakeExceptionStore(new ExceptionEntry
        {
            Guid = EntryGuid,
            ApplicationName = Marker,
            Type = Marker,
            Message = Marker,
            DuplicateCount = 1,
            LastLogDate = DateTime.UtcNow,
        });
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton<IExceptionStore>(store);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaExceptional("/exceptions", o =>
                    {
                        o.Authorize = _ =>
                        {
                            Interlocked.Increment(ref probe.AuthorizeCalls);
                            return Task.FromResult(authorized);
                        };
                        configure?.Invoke(o);
                    }));
                });
            })
            .StartAsync();
        return host.GetTestClient();
    }

    [Fact]
    public async Task Css_ShouldBeDenied_WhenAllowAnonymousAssetsIsNotSet()
    {
        var client = await ServerAsync(new Probe(), configure: null);

        var res = await client.GetAsync("/exceptions/dashboard.css");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Css_ShouldBeServedWithoutCallingAuthorize_WhenAllowAnonymousAssetsIsSet()
    {
        var probe = new Probe();
        var client = await ServerAsync(probe, o => o.AllowAnonymousAssets = true);

        var res = await client.GetAsync("/exceptions/dashboard.css");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("text/css", res.Content.Headers.ContentType!.ToString());
        Assert.Equal(0, probe.AuthorizeCalls);
    }

    [Fact]
    public async Task Css_ShouldBeServedWithoutAuthorize_WhenAllowAnonymousAssetsIsSetAndAuthorizeIsUnset()
    {
        var client = await ServerAsync(new Probe(), o =>
        {
            o.Authorize = null;
            o.AllowAnonymousAssets = true;
        });

        var res = await client.GetAsync("/exceptions/dashboard.css");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // The opt-in must add nothing but the constant stylesheet: the body is byte-identical to what an
    // authorized caller gets, carries no stored entry data, and stays uncacheable.
    [Fact]
    public async Task Css_ShouldCarryOnlyTheStylesheet_WhenServedAnonymously()
    {
        var anonymous = await ServerAsync(new Probe(), o => o.AllowAnonymousAssets = true);
        var authorized = await ServerAsync(new Probe(), configure: null, authorized: true);

        var anon = await anonymous.GetAsync("/exceptions/dashboard.css");
        var auth = await authorized.GetAsync("/exceptions/dashboard.css");

        var body = await anon.Content.ReadAsStringAsync();
        Assert.Equal(await auth.Content.ReadAsStringAsync(), body);
        Assert.DoesNotContain(Marker, body, StringComparison.Ordinal);
        Assert.Contains("no-store", anon.Headers.CacheControl!.ToString());
        Assert.Contains("Cookie", anon.Headers.Vary.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/exceptions")]
    [InlineData("/exceptions/")]
    [InlineData("/exceptions/0f8fad5b-d9cb-469f-a165-70867728950e")]
    public async Task Pages_ShouldStayDenied_WhenAllowAnonymousAssetsIsSet(string url)
    {
        var client = await ServerAsync(new Probe(), o => o.AllowAnonymousAssets = true);

        var res = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain(Marker, await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Actions_ShouldStayDenied_WhenAllowAnonymousAssetsIsSet(bool enableActions)
    {
        var client = await ServerAsync(new Probe(), o =>
        {
            o.AllowAnonymousAssets = true;
            o.EnableActions = enableActions;
        });

        foreach (var action in new[] { "protect", "delete", "hard-delete" })
        {
            var res = await client.PostAsync($"/exceptions/{EntryGuid}/{action}", content: null);

            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    // The skip matches the exact stylesheet route only. A prefix, a suffix, a trailing slash, another
    // casing, a nested path or an encoded / dot-segment spelling is not the asset and must still go
    // through Authorize (here: deny). Each variant either 404s at routing or reaches a gated handler.
    [Theory]
    [InlineData("/exceptions/dashboard.css/")]
    [InlineData("/exceptions/dashboard.cssx")]
    [InlineData("/exceptions/xdashboard.css")]
    [InlineData("/exceptions/Dashboard.css")]
    [InlineData("/exceptions/dashboard.CSS")]
    [InlineData("/exceptions/dashboard.css/extra")]
    [InlineData("/exceptions/sub/dashboard.css")]
    [InlineData("/exceptions//dashboard.css")]
    [InlineData("/exceptions/dashboard.css%2f")]
    [InlineData("/exceptions/dashboard%2fcss")]
    [InlineData("/exceptions/%2e%2e/exceptions")]
    [InlineData("/exceptions/..%2fexceptions")]
    [InlineData("/exceptions/x/../0f8fad5b-d9cb-469f-a165-70867728950e")]
    [InlineData("/exceptions/dashboard.css;x=1")]
    public async Task Variants_ShouldNotSkipAuthorize_WhenPathIsNotTheExactStylesheet(string url)
    {
        var client = await ServerAsync(new Probe(), o => o.AllowAnonymousAssets = true);

        var res = await client.GetAsync(url);

        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain(Marker, await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Css_ShouldHonourAuthorize_WhenAllowAnonymousAssetsIsNotSet()
    {
        var probe = new Probe();
        var client = await ServerAsync(probe, configure: null, authorized: true);

        var res = await client.GetAsync("/exceptions/dashboard.css");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, probe.AuthorizeCalls);
    }
}
