using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Quartz.Impl;
using Themia.Quartz;
using Themia.Quartz.Dashboard;
using Themia.Quartz.Dashboard.TypeHandlers;
using Xunit;

namespace Themia.Quartz.Tests.Json;

/// <summary>
/// HTTP-level pins for the JobDataMap endpoints that depend on JSON serialization.
/// A permanent compatibility pin for the dashboard endpoints (now System.Text.Json).
/// </summary>
public sealed class JobDataMapEndpointTests : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly IScheduler _scheduler;

    public JobDataMapEndpointTests()
    {
        (_host, _scheduler) = StartHostSync();
    }

    private static (IHost host, IScheduler scheduler) StartHostSync()
    {
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"JobDataMapTests_{Guid.NewGuid():N}",
        });
        var scheduler = factory.GetScheduler().GetAwaiter().GetResult();
        scheduler.Start().GetAwaiter().GetResult();

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddThemiaQuartz(o =>
                    {
                        o.Scheduler = scheduler;
                        o.VirtualPathRoot = "/jobs";
                        o.Authorize = _ => Task.FromResult(true); // allow all
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
            .Build();

        host.Start();
        return (host, scheduler);
    }

    private HttpClient CreateClient() => _host.GetTestClient();

    public async ValueTask DisposeAsync()
    {
        await _scheduler.Shutdown(waitForJobsToComplete: false);
        _host.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // TypeHandlers.js — pins the $typeHandlerScripts wire format
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TypeHandlersJs_ReturnsJavaScriptContentType()
    {
        var response = await CreateClient().GetAsync("/jobs/JobDataMap/TypeHandlers.js");

        response.EnsureSuccessStatusCode();
        Assert.Contains("application/javascript", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task TypeHandlersJs_ContainsVariableDeclaration()
    {
        var response = await CreateClient().GetAsync("/jobs/JobDataMap/TypeHandlers.js");
        var content = await response.Content.ReadAsStringAsync();

        // The JS variable declaration — templates depend on this exact name
        Assert.Contains("var $typeHandlerScripts =", content);
    }

    [Fact]
    public async Task TypeHandlersJs_ContainsRegisteredTypeIdKeys()
    {
        var response = await CreateClient().GetAsync("/jobs/JobDataMap/TypeHandlers.js");
        var content = await response.Content.ReadAsStringAsync();

        // Each handler with a script must appear as a key in the JS object.
        // The keys are the TypeId (full type name). Handlers with empty scripts are excluded.
        // From StandardTypes + UnsupportedTypeHandler:
        // StringHandler has a script → its TypeId must appear
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.StringHandler", content);

        // BooleanHandler has a script
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.BooleanHandler", content);

        // DateTimeHandler has a script
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.DateTimeHandler", content);

        // FileHandler has a script
        Assert.Contains("Themia.Quartz.Dashboard.TypeHandlers.FileHandler", content);
    }

    [Fact]
    public async Task TypeHandlersJs_KeysAreWrappedInFunctionBody()
    {
        // Each value in $typeHandlerScripts is a function(f){...} — the JRaw wrapping.
        // This is critical for the migration: the value must NOT be a JSON string but raw JS.
        var response = await CreateClient().GetAsync("/jobs/JobDataMap/TypeHandlers.js");
        var content = await response.Content.ReadAsStringAsync();

        // Values are wrapped in function(f){...} via JRaw — not quoted strings
        Assert.Contains("function(f)", content);

        // The exec stub that calls init
        Assert.Contains("if (f === 'init'", content);
    }

    [Fact]
    public async Task TypeHandlersJs_NumberHandler_IsAbsent_BecauseScriptIsEmpty()
    {
        // NumberHandler is registered with Script = "" — so it must NOT appear in $typeHandlerScripts.
        // This pins that empty-script handlers are correctly excluded from the output.
        var response = await CreateClient().GetAsync("/jobs/JobDataMap/TypeHandlers.js");
        var content = await response.Content.ReadAsStringAsync();

        // NumberHandler should not appear as a key (it has Script = "")
        Assert.DoesNotContain("Themia.Quartz.Dashboard.TypeHandlers.NumberHandler", content);
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    // ChangeType — String → Number conversion
    // ──────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeType_StringToNumber_ReturnsHtmlFragment()
    {
        // Build a real TypeHandlerService to get the serialized tokens
        var options = new ThemiaQuartzOptions();
        var svc = Services.Create(options).TypeHandlers;

        var stringHandler = new StringHandler { Name = "String" };
        var numberHandler = new NumberHandler(NumberHandler.UnderlyingType.Integer);
        numberHandler.Name = "Integer";
        numberHandler.DisplayName = "Integer";

        var selectedToken = svc.Serialize(stringHandler);
        var targetToken = svc.Serialize(numberHandler);

        var formContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("selected-type", selectedToken),
            new KeyValuePair<string, string>("target-type", targetToken),
            // data-map row: key "data-map[value]:row0" → value "42"
            new KeyValuePair<string, string>("data-map[value]:row0", "42"),
        });

        var response = await CreateClient().PostAsync("/jobs/JobDataMap/ChangeType", formContent);

        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();

        // ChangeType returns Html(...) — must be non-empty text/html
        Assert.NotEmpty(content);
        Assert.Contains("text/html", response.Content.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task ChangeType_WithInvalidBase64Token_ReturnsBadRequest()
    {
        // If the token can't be deserialized (bad type in JSON) the controller returns 400.
        // This pins the error-handling path.
        var formContent = new FormUrlEncodedContent(new[]
        {
            // A valid base64 string but with an unknown TypeId — triggers the BadRequest path
            new KeyValuePair<string, string>("selected-type", Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("{\"TypeId\":\"Unknown.Type\"}"))),
            new KeyValuePair<string, string>("target-type", Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes("{\"TypeId\":\"Unknown.Type\"}"))),
        });

        var response = await CreateClient().PostAsync("/jobs/JobDataMap/ChangeType", formContent);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
