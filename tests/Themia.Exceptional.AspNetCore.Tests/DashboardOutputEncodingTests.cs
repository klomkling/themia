using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

/// <summary>
/// Pins output encoding on the exceptions dashboard (coord #0237). The apps allow the GET filter form on the
/// proxied dashboard, so an injected live &lt;form&gt; is the remaining risk: every value an operator, a request or
/// a stored exception can set must reach the page HTML/attribute-encoded, through the real endpoints.
/// </summary>
public sealed class DashboardOutputEncodingTests
{
    private const string Hostile = "\"><form action=//evil>";
    private const string HostileEncoded = "&quot;&gt;&lt;form action=//evil&gt;";
    private const string LiveInjectedForm = "<form action=//evil";
    private static readonly Guid EntryGuid = new("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static async Task<HttpClient> ServerAsync(IExceptionStore store)
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
                    app.UseEndpoints(e => e.MapThemiaExceptional("/exceptions", o => o.Authorize = _ => Task.FromResult(true)));
                });
            })
            .StartAsync();
        return host.GetTestClient();
    }

    private static ExceptionEntry HostileEntry() => new()
    {
        Guid = EntryGuid,
        ApplicationName = Hostile,
        TenantId = Hostile,
        MachineName = Hostile,
        Type = Hostile,
        Message = Hostile,
        Url = Hostile,
        Host = Hostile,
        IpAddress = Hostile,
        Source = Hostile,
        HttpMethod = Hostile,
        RequestBody = Hostile,
        Detail = JsonSerializer.Serialize(new
        {
            StackTrace = Hostile,
            Inner = Hostile,
            Data = new Dictionary<string, string> { [Hostile] = Hostile },
        }),
        RequestContext = JsonSerializer.Serialize(new
        {
            headers = new Dictionary<string, string> { [Hostile] = Hostile },
            cookies = new Dictionary<string, string> { ["session"] = Hostile },
        }),
        DuplicateCount = 1,
        LastLogDate = DateTime.UtcNow,
    };

    [Fact]
    public async Task List_ShouldEncodeFilterInputs_WhenRequestParametersHoldMarkup()
    {
        var client = await ServerAsync(new FakeExceptionStore(HostileEntry()));
        var query = $"q={Uri.EscapeDataString(Hostile)}&app={Uri.EscapeDataString(Hostile)}&tenant={Uri.EscapeDataString(Hostile)}";

        var html = await client.GetStringAsync($"/exceptions?{query}");

        Assert.DoesNotContain(LiveInjectedForm, html, StringComparison.Ordinal);
        Assert.Contains($"name=\"q\" value=\"{HostileEncoded}\"", html, StringComparison.Ordinal);
        Assert.Contains($"name=\"app\" value=\"{HostileEncoded}\"", html, StringComparison.Ordinal);
        Assert.Contains($"name=\"tenant\" value=\"{HostileEncoded}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ShouldEncodeRowFields_WhenStoredEntryHoldsMarkup()
    {
        var client = await ServerAsync(new FakeExceptionStore(HostileEntry()));

        var html = await client.GetStringAsync("/exceptions");

        Assert.DoesNotContain(LiveInjectedForm, html, StringComparison.Ordinal);
        Assert.Contains($"<td>{HostileEncoded}</td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ShouldKeepPagerLinksInsideTheHrefAttribute_WhenFiltersHoldMarkup()
    {
        var store = new FakeExceptionStore(HostileEntry(), HostileEntry());
        var client = await ServerAsync(store);

        var html = await client.GetStringAsync($"/exceptions?pageSize=1&q={Uri.EscapeDataString(Hostile)}");

        Assert.DoesNotContain(LiveInjectedForm, html, StringComparison.Ordinal);
        Assert.Contains(">Next</a>", html, StringComparison.Ordinal);
        Assert.Contains($"q={Uri.EscapeDataString(Hostile)}", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_ShouldEncodeEveryField_WhenStoredEntryHoldsMarkup()
    {
        var client = await ServerAsync(new FakeExceptionStore(HostileEntry()));

        var html = await client.GetStringAsync($"/exceptions/{EntryGuid}");

        Assert.DoesNotContain(LiveInjectedForm, html, StringComparison.Ordinal);
        Assert.Contains($"<p>{HostileEncoded}</p>", html, StringComparison.Ordinal);                  // message
        Assert.Contains($"<h1 class=\"type type-err\">{HostileEncoded}</h1>", html, StringComparison.Ordinal); // type
        Assert.Contains($"<th>Url</th><td>{HostileEncoded}</td>", html, StringComparison.Ordinal);
        Assert.Contains($"<h2>Stack Trace</h2><pre>{HostileEncoded}</pre>", html, StringComparison.Ordinal);
        Assert.Contains($"<h2>Inner Exception</h2><pre>{HostileEncoded}</pre>", html, StringComparison.Ordinal);
        Assert.Contains($"<h2>Request Body</h2><pre>{HostileEncoded}</pre>", html, StringComparison.Ordinal);
        Assert.Contains($"<tr><th>{HostileEncoded}</th><td>{HostileEncoded}</td></tr>", html, StringComparison.Ordinal); // header row
    }

    [Fact]
    public async Task Detail_ShouldEncodeRawDetail_WhenDetailIsNotJson()
    {
        var entry = HostileEntry();
        entry.Detail = Hostile;
        var client = await ServerAsync(new FakeExceptionStore(entry));

        var html = await client.GetStringAsync($"/exceptions/{EntryGuid}");

        Assert.DoesNotContain(LiveInjectedForm, html, StringComparison.Ordinal);
        Assert.Contains($"<h2>Detail</h2><pre>{HostileEncoded}</pre>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_ShouldRenderNoChromeMarkup_WhenHeadHtmlAndBodyStartHtmlAreNotConfigured()
    {
        var client = await ServerAsync(new FakeExceptionStore(HostileEntry()));

        var html = await client.GetStringAsync("/exceptions");

        Assert.Contains("/dashboard.css\"></head><body><h1>", html, StringComparison.Ordinal);
    }
}
