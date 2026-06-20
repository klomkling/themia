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

public class ExceptionalDashboardTests
{
    private static readonly Guid KnownGuid = new("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static ExceptionEntry Sample(string message = "boom") => new()
    {
        Guid = KnownGuid,
        ApplicationName = "app",
        Type = "System.Exception",
        Message = message,
        Detail = "{}",
        DuplicateCount = 1,
    };

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
    public async Task List_NoAuthorize_Returns404()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), configure: null);
        var res = await client.GetAsync("/exceptions");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task List_AuthorizeFalse_Returns404()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(false));
        var res = await client.GetAsync("/exceptions");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task List_Authorized_Returns200_WithRows()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync("/exceptions");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("System.Exception", body);
        Assert.Contains($"/exceptions/{KnownGuid}", body);
    }

    [Fact]
    public async Task List_EncodesMessage_NoRawScript()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample("<script>alert(1)</script>")), o => o.Authorize = _ => Task.FromResult(true));
        var body = await (await client.GetAsync("/exceptions")).Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", body);
        Assert.DoesNotContain("<script>alert(1)</script>", body);
    }

    [Fact]
    public async Task List_FlowsFilterAndClampsPageSize()
    {
        var store = new FakeExceptionStore(Sample());
        var client = await ServerAsync(store, o => { o.Authorize = _ => Task.FromResult(true); o.MaxPageSize = 100; });

        await client.GetAsync("/exceptions?q=boom&app=app&tenant=t1&page=2&pageSize=9999");

        Assert.NotNull(store.LastFilter);
        Assert.Equal("boom", store.LastFilter!.Search);
        Assert.Equal("app", store.LastFilter.ApplicationName);
        Assert.Equal("t1", store.LastFilter.TenantId);
        Assert.Equal(2, store.LastFilter.Page);
        Assert.Equal(100, store.LastFilter.PageSize);
    }

    [Fact]
    public async Task Detail_KnownGuid_Returns200()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync($"/exceptions/{KnownGuid}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("System.Exception", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Detail_UnknownGuid_Returns404()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync($"/exceptions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Detail_Unauthorized_Returns404()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(false));
        var res = await client.GetAsync($"/exceptions/{KnownGuid}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Detail_NoAuthorize_Returns404()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), configure: null);
        var res = await client.GetAsync($"/exceptions/{KnownGuid}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Detail_ShowRequestBodyTrue_IncludesBody()
    {
        var entry = Sample();
        entry.RequestBody = "body-marker-abc";
        var client = await ServerAsync(new FakeExceptionStore(entry), o => { o.Authorize = _ => Task.FromResult(true); o.ShowRequestBody = true; });
        var body = await (await client.GetAsync($"/exceptions/{KnownGuid}")).Content.ReadAsStringAsync();
        Assert.Contains("body-marker-abc", body);
    }

    [Fact]
    public async Task Detail_ShowRequestBodyFalse_OmitsBody()
    {
        var entry = Sample();
        entry.RequestBody = "secret-token-xyz";
        var client = await ServerAsync(new FakeExceptionStore(entry), o => { o.Authorize = _ => Task.FromResult(true); o.ShowRequestBody = false; });
        var res = await client.GetAsync($"/exceptions/{KnownGuid}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // page still renders, just without the body
        Assert.DoesNotContain("secret-token-xyz", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task List_AuthorizeThrows_Returns404()
    {
        // A throwing Authorize predicate must fail closed (deny + hide), not 500.
        var client = await ServerAsync(new FakeExceptionStore(Sample()),
            o => o.Authorize = _ => throw new InvalidOperationException("auth backend down"));
        var res = await client.GetAsync("/exceptions");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task List_StoreThrows_NotServedAs200()
    {
        // Auth passes, then the store fails — the error must propagate, never be swallowed into a 200.
        var store = new FakeExceptionStore(Sample()) { FailWith = new InvalidOperationException("db down") };
        var client = await ServerAsync(store, o => o.Authorize = _ => Task.FromResult(true));
        try
        {
            var res = await client.GetAsync("/exceptions");
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        }
        catch (InvalidOperationException)
        {
            // TestServer may rethrow the unhandled exception — also acceptable (not a swallowed 200).
        }
    }

    [Fact]
    public async Task List_ClampsPageAndPageSizeUpToMinimum()
    {
        var store = new FakeExceptionStore(Sample());
        var client = await ServerAsync(store, o => o.Authorize = _ => Task.FromResult(true));

        await client.GetAsync("/exceptions?page=0&pageSize=0");

        Assert.Equal(1, store.LastFilter!.Page);
        Assert.Equal(1, store.LastFilter.PageSize);
    }

    [Fact]
    public async Task List_EncodesConfiguredTitle()
    {
        // The Title flows options -> DashboardHtml -> <title>/<h1>; a markup title must be encoded.
        var client = await ServerAsync(new FakeExceptionStore(Sample()),
            o => { o.Authorize = _ => Task.FromResult(true); o.Title = "<script>t()</script>"; });
        var body = await (await client.GetAsync("/exceptions")).Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;t()&lt;/script&gt;", body);
        Assert.DoesNotContain("<script>t()</script>", body);
    }

    [Fact]
    public async Task List_AuthorizeCancelled_DoesNotMaskAs404()
    {
        // OperationCanceledException is cancellation flow, not a denial — it must propagate, never 404.
        var client = await ServerAsync(new FakeExceptionStore(Sample()),
            o => o.Authorize = _ => throw new OperationCanceledException());
        try
        {
            var res = await client.GetAsync("/exceptions");
            Assert.NotEqual(HttpStatusCode.NotFound, res.StatusCode);
        }
        catch (OperationCanceledException)
        {
            // Propagated as cancellation — correct; it was NOT swallowed into a 404 deny.
        }
    }

    [Fact]
    public async Task List_MalformedDateParams_AreIgnored_Not500()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync("/exceptions?from=not-a-date&to=also-bad");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public void MapThemiaExceptional_RejectsInvalidPaging()
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s => { s.AddRouting(); s.AddSingleton<IExceptionStore>(new FakeExceptionStore()); });
                web.Configure(app =>
                {
                    app.UseRouting();
                    // MaxPageSize < DefaultPageSize must be rejected at mount.
                    app.UseEndpoints(e => e.MapThemiaExceptional("/exceptions", o => o.MaxPageSize = 0));
                });
            })
            .Build();

        // The mount-time validation throws during pipeline build; the host may wrap it, so walk the chain.
        var ex = Assert.ThrowsAny<Exception>(() => host.Start());
        var found = false;
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is ArgumentOutOfRangeException) { found = true; break; }
        }
        Assert.True(found, $"Expected ArgumentOutOfRangeException in the chain, got: {ex}");
    }
}
