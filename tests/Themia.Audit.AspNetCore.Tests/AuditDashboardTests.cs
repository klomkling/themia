using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Themia.Audit.AspNetCore.Tests;

public class AuditDashboardTests
{
    private static readonly Guid ExistingUid = new("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static AuditEntry Sample(string eventType = "PROPOSAL_ACCEPTED", string? data = null, Guid? eventUid = null, DateTimeOffset? occurredAt = null) => new()
    {
        EventUid = eventUid ?? ExistingUid,
        Category = AuditCategory.Activity,
        EventType = eventType,
        Outcome = AuditOutcome.Success,
        ActorId = "user-1",
        OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        Data = data,
    };

    private static async Task<HttpClient> ServerAsync(FakeAuditStore store, Action<AuditDashboardOptions>? configure)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton<IAuditStore>(store);
                    s.AddSingleton<IAuditDialect>(new FakeAuditDialect());
                    s.Configure<AuditOptions>(o => o.ConnectionString = "fake-connection-string");
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaAuditDashboard("/audit", configure));
                });
            })
            .StartAsync();
        return host.GetTestClient();
    }

    private static int CountRows(string html) => html.Split("<tr class=\"audit-row\"").Length - 1;

    // ---- Step 1: fail-closed security tests ----

    [Fact]
    public async Task Null_authorize_denies_the_list()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), configure: null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/audit")).StatusCode);
    }

    [Fact]
    public async Task Null_authorize_denies_the_detail_route_for_a_row_that_exists()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), configure: null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/audit/{ExistingUid}")).StatusCode);
    }

    [Fact]
    public async Task A_throwing_authorize_fails_closed_not_open()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => throw new InvalidOperationException());
        var res = await client.GetAsync("/audit");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain("themia", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Authorize_false_denies_the_list()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(false));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/audit")).StatusCode);
    }

    [Fact]
    public async Task Authorize_false_denies_the_detail_route()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(false));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/audit/{ExistingUid}")).StatusCode);
    }

    [Fact]
    public async Task Authorized_list_returns_200_with_rows()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync("/audit");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("PROPOSAL_ACCEPTED", body);
        Assert.Contains($"/audit/{ExistingUid}", body);
    }

    [Fact]
    public async Task Authorized_detail_returns_200()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync($"/audit/{ExistingUid}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("PROPOSAL_ACCEPTED", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Detail_unknown_event_uid_returns_404()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync($"/audit/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task List_encodes_event_type_no_raw_script()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample("<script>alert(1)</script>")), o => o.Authorize = _ => Task.FromResult(true));
        var body = await (await client.GetAsync("/audit")).Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", body);
        Assert.DoesNotContain("<script>alert(1)</script>", body);
    }

    [Fact]
    public async Task Denied_invokes_on_denied_instead_of_bare_404()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o =>
        {
            o.Authorize = _ => Task.FromResult(false);
            o.OnDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status302Found;
                ctx.Response.Headers.Location = "/login?returnUrl=/audit";
                return Task.CompletedTask;
            };
        });

        var res = await client.GetAsync("/audit");

        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/login?returnUrl=/audit", res.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Denied_when_on_denied_throws_fails_closed()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o =>
        {
            o.Authorize = _ => Task.FromResult(false);
            o.OnDenied = _ => throw new InvalidOperationException("redirect target misconfigured");
        });

        var res = await client.GetAsync("/audit");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_event_uid_does_not_invoke_on_denied()
    {
        // A missing entry is a genuine 404, not an authorization denial.
        var denied = false;
        var client = await ServerAsync(new FakeAuditStore(Sample()), o =>
        {
            o.Authorize = _ => Task.FromResult(true);
            o.OnDenied = _ => { denied = true; return Task.CompletedTask; };
        });

        var res = await client.GetAsync($"/audit/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.False(denied, "a not-found event must not be treated as an authorization denial");
    }

    [Fact]
    public async Task Authorize_cancelled_does_not_mask_as_404()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => throw new OperationCanceledException());
        try
        {
            var res = await client.GetAsync("/audit");
            Assert.NotEqual(HttpStatusCode.NotFound, res.StatusCode);
        }
        catch (OperationCanceledException)
        {
            // Propagated as cancellation — correct; not swallowed into a deny.
        }
    }

    [Fact]
    public async Task List_is_not_cacheable()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync("/audit");

        var cacheControl = res.Headers.CacheControl!;
        Assert.True(cacheControl.NoStore, "dashboard HTML must be Cache-Control: no-store (also disables bfcache)");
        Assert.True(cacheControl.NoCache);
    }

    [Fact]
    public void MapThemiaAuditDashboard_rejects_invalid_paging()
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton<IAuditStore>(new FakeAuditStore());
                    s.AddSingleton<IAuditDialect>(new FakeAuditDialect());
                    s.Configure<AuditOptions>(o => o.ConnectionString = "fake");
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaAuditDashboard("/audit", o => o.MaxPageSize = 0));
                });
            })
            .Build();

        var ex = Assert.ThrowsAny<Exception>(() => host.Start());
        var found = false;
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is ArgumentOutOfRangeException) { found = true; break; }
        }
        Assert.True(found, $"Expected ArgumentOutOfRangeException in the chain, got: {ex}");
    }

    // ---- Step 5: disclosure and paging tests ----

    [Fact]
    public async Task ShowData_false_keeps_the_payload_off_the_page()
    {
        var client = await ServerAsync(
            new FakeAuditStore(Sample(data: "{\"note\":\"SENTINEL-PAYLOAD\"}")),
            o => o.Authorize = _ => Task.FromResult(true)); // ShowData defaults to false
        var body = await client.GetStringAsync($"/audit/{ExistingUid}");
        Assert.DoesNotContain("SENTINEL-PAYLOAD", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowData_true_renders_the_payload()
    {
        var client = await ServerAsync(
            new FakeAuditStore(Sample(data: "{\"note\":\"SENTINEL-PAYLOAD\"}")),
            o => { o.Authorize = _ => Task.FromResult(true); o.ShowData = true; });
        var body = await client.GetStringAsync($"/audit/{ExistingUid}");
        Assert.Contains("SENTINEL-PAYLOAD", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_size_is_clamped_to_the_maximum()
    {
        var entries = Enumerable.Range(0, 500)
            .Select(i => Sample($"EVT_{i}", eventUid: Guid.NewGuid(), occurredAt: DateTimeOffset.UtcNow.AddSeconds(-i)))
            .ToArray();
        var store = new FakeAuditStore(entries);
        AuditDashboardOptions? captured = null;
        var client = await ServerAsync(store, o => { o.Authorize = _ => Task.FromResult(true); captured = o; });

        var html = await client.GetStringAsync("/audit?pageSize=100000");

        Assert.Equal(captured!.MaxPageSize, CountRows(html));
    }

    [Fact]
    public async Task Detail_does_not_resolve_a_sequential_id()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/audit/1")).StatusCode);
    }

    [Fact]
    public async Task List_flows_filter_and_clamps_page_size()
    {
        var store = new FakeAuditStore(Sample());
        var client = await ServerAsync(store, o => { o.Authorize = _ => Task.FromResult(true); o.MaxPageSize = 100; });

        await client.GetAsync("/audit?tenant=t1&actor=user-1&entityType=Deal&entityId=42&page=2&pageSize=9999");

        Assert.NotNull(store.LastQuery);
        Assert.Equal("t1", store.LastQuery!.TenantId);
        Assert.Equal("user-1", store.LastQuery.ActorId);
        Assert.Equal("Deal", store.LastQuery.EntityType);
        Assert.Equal("42", store.LastQuery.EntityId);
        Assert.Equal(2, store.LastQuery.Page);
        Assert.Equal(100, store.LastQuery.PageSize);
    }

    [Fact]
    public async Task List_clamps_page_and_page_size_up_to_minimum()
    {
        var store = new FakeAuditStore(Sample());
        var client = await ServerAsync(store, o => o.Authorize = _ => Task.FromResult(true));

        await client.GetAsync("/audit?page=0&pageSize=0");

        Assert.Equal(1, store.LastQuery!.Page);
        Assert.Equal(1, store.LastQuery.PageSize);
    }

    [Fact]
    public async Task List_malformed_date_params_are_ignored_not_500()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync("/audit?from=not-a-date&to=also-bad");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task List_encodes_configured_title()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()),
            o => { o.Authorize = _ => Task.FromResult(true); o.Title = "<script>t()</script>"; });
        var body = await (await client.GetAsync("/audit")).Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;t()&lt;/script&gt;", body);
        Assert.DoesNotContain("<script>t()</script>", body);
    }

    [Fact]
    public async Task List_flows_heading_separate_from_title()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()),
            o => { o.Authorize = _ => Task.FromResult(true); o.Title = "Contoso Audit"; o.Heading = "Audit"; });
        var body = await (await client.GetAsync("/audit")).Content.ReadAsStringAsync();

        Assert.Contains("<title>Contoso Audit</title>", body, StringComparison.Ordinal);
        Assert.Contains("<h1>Audit</h1>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_flows_chrome_slots()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()),
            o =>
            {
                o.Authorize = _ => Task.FromResult(true);
                o.HeadHtml = "<meta name=\"viewport\" content=\"width=device-width\">";
                o.BodyStartHtml = "<header id=\"app-chrome\"><a href=\"/admin\">Back</a></header>";
            });
        var body = await (await client.GetAsync("/audit")).Content.ReadAsStringAsync();
        Assert.Contains("<meta name=\"viewport\" content=\"width=device-width\">", body, StringComparison.Ordinal);
        Assert.Contains("<header id=\"app-chrome\"><a href=\"/admin\">Back</a></header>", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_flows_custom_stylesheet_and_favicon()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()),
            o => { o.Authorize = _ => Task.FromResult(true); o.CustomStyleSheet = "/app/theme.css"; o.CustomFavicon = "/app/fav.ico"; });
        var body = await (await client.GetAsync("/audit")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/app/theme.css\"", body);
        Assert.Contains("href=\"/app/fav.ico\"", body);
    }
}
