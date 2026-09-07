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
        // "themia" appears in neither a successful render nor the empty 404 body, so asserting its
        // absence proves nothing — a complete leak of the row below would pass it too. Assert the
        // absence of the actual event data instead: present on a genuine 200 (Authorized_list_returns_200_with_rows),
        // and this is exactly what must never reach an unauthorized caller.
        Assert.DoesNotContain("PROPOSAL_ACCEPTED", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
    public async Task Denied_when_on_denied_throws_after_writing_does_not_throw_from_the_request()
    {
        // A hook that has already written to the response before throwing leaves nothing for the fallback
        // to clear or salvage: HttpResponse.Clear() itself throws once the response has started, so the
        // deny path must not call it — doing so would turn one exception into a worse, unhandled one.
        var client = await ServerAsync(new FakeAuditStore(Sample()), o =>
        {
            o.Authorize = _ => Task.FromResult(false);
            o.OnDenied = async ctx =>
            {
                await ctx.Response.WriteAsync("partial");
                throw new InvalidOperationException("boom after the response started");
            };
        });

        // Must complete without an unhandled exception escaping the request.
        var res = await client.GetAsync("/audit");
        Assert.NotNull(res);
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
    public async Task Authorize_cancelled_does_not_mask_as_404_or_leak_the_dashboard()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => throw new OperationCanceledException());
        try
        {
            var res = await client.GetAsync("/audit");
            // NotEqual(NotFound) alone passes just as well on a 200 that serves the whole dashboard to an
            // unauthorized caller — that is not "not masked as a deny", it is a worse failure. Pin down
            // that no successful, data-bearing response comes back either.
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
            Assert.DoesNotContain("PROPOSAL_ACCEPTED", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            // Propagated as cancellation — correct; not swallowed into a deny or an allow.
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
    public async Task Null_authorize_denies_the_stylesheet()
    {
        // The stylesheet must be gated like the list/detail routes: an unauthenticated 200 here would
        // confirm the mount path exists, which the route-hiding 404 exists to conceal.
        var client = await ServerAsync(new FakeAuditStore(Sample()), configure: null);
        var res = await client.GetAsync("/audit/dashboard.css");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain("{", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Denied_stylesheet_is_not_cacheable()
    {
        // A shared cache serving this 404 back to an authorized admin would leave the dashboard unstyled;
        // no-store must accompany the deny status, not just the status code.
        var client = await ServerAsync(new FakeAuditStore(Sample()), configure: null);
        var res = await client.GetAsync("/audit/dashboard.css");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.True(res.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Authorized_stylesheet_returns_200_css()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync("/audit/dashboard.css");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/css", res.Content.Headers.ContentType!.MediaType);
        Assert.Contains("{", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authorized_stylesheet_is_not_cacheable()
    {
        // A shared cache serving this 200 back to an unauthenticated prober would confirm the mount path
        // exists without Authorize ever running — the exact hole the route-hiding 404 exists to close.
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync("/audit/dashboard.css");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Denied_list_is_not_cacheable()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), configure: null);
        var res = await client.GetAsync("/audit");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.True(res.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Authorized_detail_is_not_cacheable_and_varies()
    {
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync($"/audit/{ExistingUid}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.CacheControl!.NoStore);
        var vary = res.Headers.Vary.ToString();
        Assert.Contains("Cookie", vary);
        Assert.Contains("Authorization", vary);
    }

    [Fact]
    public async Task Detail_unknown_event_uid_404_is_not_cacheable_and_varies()
    {
        // An authorized-but-not-found 404 is heuristically cacheable under RFC 9111 §4.2.2 — without
        // no-store a shared proxy can pin "no such event" and keep serving it after the row exists.
        var client = await ServerAsync(new FakeAuditStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync($"/audit/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.True(res.Headers.CacheControl!.NoStore);
        var vary = res.Headers.Vary.ToString();
        Assert.Contains("Cookie", vary);
        Assert.Contains("Authorization", vary);
    }

    [Fact]
    public async Task PreventCaching_appends_to_an_existing_vary_header_instead_of_replacing_it()
    {
        // Simulates a middleware upstream of the dashboard (e.g. response compression) that already set
        // its own Vary entry. Assigning (rather than appending) would wipe it out, making a compressed and
        // an uncompressed response interchangeable in a shared cache.
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton<IAuditStore>(new FakeAuditStore(Sample()));
                    s.AddSingleton<IAuditDialect>(new FakeAuditDialect());
                    s.Configure<AuditOptions>(o => o.ConnectionString = "fake-connection-string");
                });
                web.Configure(app =>
                {
                    app.Use(async (ctx, next) =>
                    {
                        ctx.Response.Headers.Append("Vary", "Accept-Encoding");
                        await next();
                    });
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaAuditDashboard("/audit", o => o.Authorize = _ => Task.FromResult(true)));
                });
            })
            .StartAsync();

        var res = await host.GetTestClient().GetAsync("/audit");

        var vary = res.Headers.Vary.ToString();
        Assert.Contains("Accept-Encoding", vary);
        Assert.Contains("Cookie", vary);
        Assert.Contains("Authorization", vary);
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
    public async Task Without_a_scope_hook_the_query_string_tenant_flows_unscoped()
    {
        // Documents today's default rather than leaving it incidental: with no ScopeQuery configured, a
        // caller who passes Authorize can read any tenant simply by editing ?tenant= — Authorize only
        // decides yes/no, it cannot narrow a query.
        var store = new FakeAuditStore(Sample());
        var client = await ServerAsync(store, o => o.Authorize = _ => Task.FromResult(true));

        await client.GetAsync("/audit?tenant=someone-elses-tenant");

        Assert.Equal("someone-elses-tenant", store.LastQuery!.TenantId);
    }

    [Fact]
    public async Task ScopeQuery_runs_after_authorize_and_is_honoured()
    {
        var store = new FakeAuditStore(Sample());
        var client = await ServerAsync(store, o =>
        {
            o.Authorize = _ => Task.FromResult(true);
            o.ScopeQuery = (_, query) => query with { TenantId = "tenant-a" };
        });

        await client.GetAsync("/audit");

        Assert.Equal("tenant-a", store.LastQuery!.TenantId);
    }

    [Fact]
    public async Task ScopeQuery_fixed_tenant_cannot_be_overridden_from_the_query_string()
    {
        var store = new FakeAuditStore(Sample());
        var client = await ServerAsync(store, o =>
        {
            o.Authorize = _ => Task.FromResult(true);
            o.ScopeQuery = (_, query) => query with { TenantId = "tenant-a" };
        });

        // A viewer scoped to tenant-a tries to read tenant-b's rows by editing the URL.
        await client.GetAsync("/audit?tenant=tenant-b");

        Assert.Equal("tenant-a", store.LastQuery!.TenantId);
    }

    [Fact]
    public async Task ScopeQuery_allows_the_detail_route_when_the_row_matches()
    {
        var entry = Sample() with { TenantId = "tenant-a" };
        var client = await ServerAsync(new FakeAuditStore(entry), o =>
        {
            o.Authorize = _ => Task.FromResult(true);
            o.ScopeQuery = (_, query) => query with { TenantId = "tenant-a" };
        });

        var res = await client.GetAsync($"/audit/{ExistingUid}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("PROPOSAL_ACCEPTED", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ScopeQuery_hides_the_detail_route_for_a_row_outside_the_scope()
    {
        // A viewer scoped to tenant-a must not be able to read tenant-b's row just by knowing (or
        // guessing) its event_uid — the same isolation ScopeQuery already gives the list route.
        var entry = Sample() with { TenantId = "tenant-b" };
        var client = await ServerAsync(new FakeAuditStore(entry), o =>
        {
            o.Authorize = _ => Task.FromResult(true);
            o.ScopeQuery = (_, query) => query with { TenantId = "tenant-a" };
        });

        var res = await client.GetAsync($"/audit/{ExistingUid}");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain("PROPOSAL_ACCEPTED", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
    // The pager used to emit only ?page= and ?pageSize=, so Prev/Next silently dropped every filter: the
    // viewer landed on page two of the UNFILTERED list while the "N total" they had just read was counted
    // for the filtered one. Two numbers disagreeing on one screen is what makes this a correctness bug.
    [Fact]
    public async Task Pager_links_carry_every_active_filter()
    {
        // Seeded to MATCH every filter below — otherwise the fake returns nothing, there is no second
        // page, and the test would pass by rendering no pager at all.
        var matching = Enumerable.Range(0, 40)
            .Select(_ => Sample() with { TenantId = "t1", EntityType = "Deal", EntityId = "42" })
            .ToArray();
        var client = await ServerAsync(new FakeAuditStore(matching), o => o.Authorize = _ => Task.FromResult(true));

        var html = await client.GetStringAsync(
            "/audit?tenant=t1&actor=user-1&entityType=Deal&entityId=42&category=Activity&outcome=Success&pageSize=5");

        var next = NextHref(html);
        Assert.Contains("page=2", next, StringComparison.Ordinal);
        Assert.Contains("pageSize=5", next, StringComparison.Ordinal);
        Assert.Contains("tenant=t1", next, StringComparison.Ordinal);
        Assert.Contains("actor=user-1", next, StringComparison.Ordinal);
        Assert.Contains("entityType=Deal", next, StringComparison.Ordinal);
        Assert.Contains("entityId=42", next, StringComparison.Ordinal);
        Assert.Contains("category=Activity", next, StringComparison.Ordinal);
        Assert.Contains("outcome=Success", next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pager_links_url_encode_filter_values()
    {
        var matching = Enumerable.Range(0, 40)
            .Select(_ => Sample() with { ActorId = "a b&c" })
            .ToArray();
        var client = await ServerAsync(new FakeAuditStore(matching), o => o.Authorize = _ => Task.FromResult(true));

        var html = await client.GetStringAsync("/audit?actor=a%20b%26c&pageSize=5");

        // Encoded for the query string, then HTML-encoded for the attribute. A raw & would end the
        // parameter and silently truncate the filter on the next click.
        Assert.Contains("actor=a%20b%26c", NextHref(html), StringComparison.Ordinal);
    }

    private static string NextHref(string html)
    {
        var marker = "\">Next</a>";
        var end = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(end > 0, "the list did not render a Next link");
        var start = html.LastIndexOf("href=\"", end, StringComparison.Ordinal) + 6;
        return html[start..end];
    }
}
