using System.Net;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Impl;
using Xunit;

namespace Themia.Quartz.Tests;

/// <summary>
/// Vendoring validation gate: proves the embedded dashboard routes, the deny-all authorize gate,
/// and the embedded static-content file server all work end-to-end via a real in-process host.
/// </summary>
public sealed class DashboardSmokeTests
{
    // Owns the host + its scheduler so both are torn down deterministically. The scheduler is shut
    // down on dispose to avoid leaking Quartz background threads across (parallel) test classes.
    private sealed class TestHostScope(IHost host, IScheduler scheduler) : IAsyncDisposable
    {
        public HttpClient CreateClient() => host.GetTestClient();

        public async ValueTask DisposeAsync()
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
            host.Dispose();
        }
    }

    // Middleware order: UseThemiaQuartz (gate + static files + Services bridge) must run before
    // UseRouting so that the authorize gate and embedded content serving intercept requests before
    // the endpoint dispatcher. MapThemiaQuartz inside UseEndpoints registers the controller route.
    private static async Task<TestHostScope> StartHostAsync(
        Func<HttpContext, Task<bool>>? authorize,
        int? deniedStatus = null,
        Action<ThemiaQuartzOptions>? configure = null)
    {
        // Uniquely-named scheduler (not the process-global default) so concurrently-running test
        // classes don't share scheduler state.
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"SmokeTestScheduler_{Guid.NewGuid():N}",
        });
        var scheduler = await factory.GetScheduler();
        await scheduler.Start();

        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddThemiaQuartz(o =>
                    {
                        o.Scheduler = scheduler;
                        o.VirtualPathRoot = "/jobs";
                        o.Authorize = authorize;
                        if (deniedStatus is { } ds)
                        {
                            o.DeniedStatusCode = ds;
                        }

                        configure?.Invoke(o);
                    });
                    services.AddSingleton<IExecutionHistoryStore>(new InProcExecutionHistoryStore());
                });
                web.Configure(app =>
                {
                    app.UseThemiaQuartz();
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaQuartz());
                });
            })
            .StartAsync();

        return new TestHostScope(host, scheduler);
    }

    [Fact]
    public async Task DeniedByDefault_Returns404()
    {
        // Unset Authorize (null) is the documented deny-all default — validate THAT, not an explicit false.
        // Default deny status is 404 (hides the route from unauthenticated probes; matches the
        // Themia.Exceptional dashboard).
        await using var scope = await StartHostAsync(authorize: null);
        var response = await scope.CreateClient().GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Denied_HonorsConfiguredStatusCode()
    {
        await using var scope = await StartHostAsync(authorize: null, deniedStatus: StatusCodes.Status403Forbidden);
        var response = await scope.CreateClient().GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Denied_WhenAuthorizeThrows_FailsClosed()
    {
        // A throwing Authorize predicate must fail closed (deny status), not surface a 500.
        await using var scope = await StartHostAsync(authorize: _ => throw new InvalidOperationException("auth bug"));
        var response = await scope.CreateClient().GetAsync("/jobs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SchedulerIndex_WhenAuthorized_Returns200WithHtml()
    {
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));
        var response = await scope.CreateClient().GetAsync("/jobs/Scheduler/Index");
        response.EnsureSuccessStatusCode();
        Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task Layout_EmitsChromeSlots_Verbatim()
    {
        // The two raw-HTML slots are adopter-authored markup (back-link, dark-mode toggle): they must
        // reach the page unencoded, HeadHtml inside <head> and BodyStartHtml right after <body> opens.
        const string head = "<meta name=\"x-chrome\" content=\"1\">";
        const string bodyStart = "<header id=\"app-chrome\"><a href=\"/admin\">Back</a></header>";
        await using var scope = await StartHostAsync(
            authorize: _ => Task.FromResult(true),
            configure: o => { o.HeadHtml = head; o.BodyStartHtml = bodyStart; });

        var body = await (await scope.CreateClient().GetAsync("/jobs/Scheduler/Index")).Content.ReadAsStringAsync();

        Assert.Contains(head, body, StringComparison.Ordinal);
        Assert.Contains(bodyStart, body, StringComparison.Ordinal);
        Assert.True(
            body.IndexOf(head, StringComparison.Ordinal) < body.IndexOf("</head>", StringComparison.Ordinal),
            "HeadHtml must be emitted inside <head>");
        Assert.True(
            body.IndexOf(bodyStart, StringComparison.Ordinal) < body.IndexOf("id=\"top-menu\"", StringComparison.Ordinal),
            "BodyStartHtml must be emitted before the dashboard's own chrome");
    }

    [Fact]
    public async Task SchedulerIndex_StatusTiles_AreClassed_NotInlineStyled()
    {
        // Tile colours live in Content/Site.css so an adopter stylesheet can override them without
        // !important (inline style= would outrank any adopter rule).
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));

        var body = await (await scope.CreateClient().GetAsync("/jobs/Scheduler/Index")).Content.ReadAsStringAsync();

        Assert.Contains("stat-executed", body, StringComparison.Ordinal);
        Assert.Contains("stat-failed", body, StringComparison.Ordinal);
        Assert.Contains("stat-executing", body, StringComparison.Ordinal);
        Assert.DoesNotContain("linear-gradient", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".stat-executed")]
    [InlineData(".stat-failed")]
    [InlineData(".stat-executing")]
    [InlineData(".stat-activity")]
    // The counts tile is the one rule that must OUT-WEIGH a selector already in this file:
    // "#scheduler-dashboard .ui.statistic" (1,2,0) sets a box-shadow, and the inline style= this
    // replaced used to beat it unconditionally. A bare "#scheduler-dashboard .stat-counts" (1,1,0)
    // loses that cascade and the tile silently regains the shadow — assert the winning form.
    [InlineData("#scheduler-dashboard .ui.statistic.stat-counts")]
    public async Task SiteCss_DefinesRule_ForEachTileHook(string selector)
    {
        // Markup assertions alone cannot see a lost cascade: the classes can be present and correct
        // while the rule that styles them never applies. Pin the selectors the tiles depend on.
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));

        var css = await (await scope.CreateClient().GetAsync("/jobs/Content/Site.css")).Content.ReadAsStringAsync();

        Assert.Contains(selector + " {", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SiteCss_TileColourHooks_AreNotIdScoped()
    {
        // The point of moving the colours out of inline style= was to let an adopter's CustomStyleSheet
        // rule (".stat-executed", specificity 0,1,0) win. An ID-scoped built-in rule (1,1,0) would beat
        // it regardless of source order, forcing !important back on the adopter — the exact outcome the
        // change exists to remove. Nothing in semantic.min.css styles a .statistic background, so these
        // rules need no ID to land.
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));

        var css = await (await scope.CreateClient().GetAsync("/jobs/Content/Site.css")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("#scheduler-dashboard .stat-", css, StringComparison.Ordinal);
    }

    // An unset CustomFavicon/CustomStyleSheet used to emit href="" — which the browser resolves to the page
    // URL itself, i.e. it fetches the dashboard HTML and treats it as an icon / stylesheet. The favicon link
    // is emitted last, so it WON and displaced the seven bundled PNG favicons for every adopter who left the
    // option alone. Both links must simply not be emitted when unset (parity with Themia.Exceptional).
    [Fact]
    public async Task Layout_OmitsCustomAssetLinks_WhenUnset()
    {
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));

        var body = await (await scope.CreateClient().GetAsync("/jobs/Scheduler/Index")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("href=\"\"", body, StringComparison.Ordinal);
        // The bundled favicons must survive.
        Assert.Contains("Content/Images/favicons/favicon-256.png", body, StringComparison.Ordinal);
    }

    // A set CustomFavicon must WIN the browser tab. Emitting it alongside the seven bundled vendor icons
    // did not achieve that: those declare explicit sizes and the adopter's did not, so the browser's
    // size-preference algorithm picked a vendor PNG and the SilkierQuartz duck won anyway — setting the
    // option looked like a no-op. Don't compete with the vendor icons; replace them. An adopter who
    // supplies an icon has opted out of ours.
    [Fact]
    public async Task Layout_CustomFavicon_ReplacesBundledIcons_RatherThanCompetingWithThem()
    {
        await using var scope = await StartHostAsync(
            authorize: _ => Task.FromResult(true),
            configure: o => o.CustomFavicon = "/icon-192.png");

        var body = await (await scope.CreateClient().GetAsync("/jobs/Scheduler/Index")).Content.ReadAsStringAsync();

        Assert.Contains("<link rel=\"icon\" type=\"image/png\" href=\"/icon-192.png\">", body, StringComparison.Ordinal);
        // The vendor icons must be gone — otherwise they out-rank the adopter's on `sizes`.
        Assert.DoesNotContain("Content/Images/favicons/", body, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(body, "rel=\"icon\""));
    }

    [Fact]
    public async Task Layout_EmitsBundledIcons_WhenCustomFaviconUnset()
    {
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));

        var body = await (await scope.CreateClient().GetAsync("/jobs/Scheduler/Index")).Content.ReadAsStringAsync();

        Assert.Contains("Content/Images/favicons/favicon-16.png", body, StringComparison.Ordinal);
        Assert.Contains("Content/Images/favicons/favicon-256.png", body, StringComparison.Ordinal);
        Assert.Equal(7, Regex.Matches(body, "rel=\"icon\"").Count);
    }

    [Theory]
    [InlineData("/icon.svg", "type=\"image/svg+xml\" ")]
    [InlineData("/icon-192.png", "type=\"image/png\" ")]
    [InlineData("/favicon.ico", "type=\"image/x-icon\" ")]
    [InlineData("/icon.png?v=2", "type=\"image/png\" ")]        // query string must not defeat the sniff
    [InlineData("/icon", "")]                                    // unknown extension: omit type, don't guess
    public async Task Layout_DerivesFaviconType_FromUrlExtension(string url, string expectedTypeAttr)
    {
        // Without a type the browser cannot tell what the icon is; hardcoding image/x-icon (the old
        // behaviour) declared a false MIME type for an adopter's PNG or SVG.
        await using var scope = await StartHostAsync(
            authorize: _ => Task.FromResult(true),
            configure: o => o.CustomFavicon = url);

        var body = await (await scope.CreateClient().GetAsync("/jobs/Scheduler/Index")).Content.ReadAsStringAsync();

        Assert.Contains($"<link rel=\"icon\" {expectedTypeAttr}href=\"{url}\">", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Layout_EmitsCustomStyleSheet_WhenSet()
    {
        await using var scope = await StartHostAsync(
            authorize: _ => Task.FromResult(true),
            configure: o => o.CustomStyleSheet = "/app/theme.css");

        var body = await (await scope.CreateClient().GetAsync("/jobs/Scheduler/Index")).Content.ReadAsStringAsync();

        Assert.Contains("href=\"/app/theme.css\"", body, StringComparison.Ordinal);
        // Still after the built-in Site.css, so adopter rules win on source order.
        Assert.True(
            body.IndexOf("Content/Site.css", StringComparison.Ordinal) < body.IndexOf("/app/theme.css", StringComparison.Ordinal),
            "the custom stylesheet must be linked after the built-in Site.css");
    }

    [Fact]
    public async Task Layout_Footer_IsClassed_NotInlineStyled()
    {
        // Leftover from the tile reclassing: an inline style= on the footer beats any adopter rule, so a dark
        // theme still ended the page in a light-grey strip unless the adopter reached for !important.
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));

        var body = await (await scope.CreateClient().GetAsync("/jobs/Scheduler/Index")).Content.ReadAsStringAsync();

        Assert.Contains("<footer class=\"dashboard-footer\">", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#f8f8f8", body, StringComparison.Ordinal);
        Assert.DoesNotContain("#909090", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".dashboard-footer")]
    [InlineData(".dashboard-footer a")]
    public async Task SiteCss_DefinesRule_ForEachFooterHook(string selector)
    {
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));

        var css = await (await scope.CreateClient().GetAsync("/jobs/Content/Site.css")).Content.ReadAsStringAsync();

        Assert.Contains(selector + " {", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbeddedContent_IsServed_WhenAuthorized()
    {
        await using var scope = await StartHostAsync(authorize: _ => Task.FromResult(true));
        // Asset path: Dashboard/Content/Lib/semanticui/semantic.min.css
        // Embedded resource: Themia.Quartz.Dashboard.Content.Lib.semanticui.semantic.min.css
        // URL: /jobs/Content/Lib/semanticui/semantic.min.css
        var response = await scope.CreateClient().GetAsync("/jobs/Content/Lib/semanticui/semantic.min.css");
        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
    }
}
